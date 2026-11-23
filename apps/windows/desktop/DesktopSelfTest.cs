using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;

namespace PrivacyBrowser.Desktop;

internal static class DesktopSelfTest
{
  internal static int WriteFixture(string path)
  {
    var snapshot = SyncSnapshot.CreateDefault();
    snapshot.Groups.Add(new TabGroupEntry("windows-group", "default", "Windows Reading"));
    snapshot.Tabs.Add(new TabEntry("windows-tab", "Windows fixture", "https://example.com/windows", "default", "windows-group"));
    SyncCodec.Write(path, snapshot, "cross-platform-pass");
    return 0;
  }

  internal static int Run()
  {
    var longShelf = Enumerable.Range(1, 316)
        .Select(index => new BookshelfItem(index.ToString(), "long", "", "", 900, 1400, index)).ToArray();
    using (var gallery = new VirtualBookshelfGallery(longShelf, 2, _ => { }, _ => { }, loadImages: false)
        { Size = new Size(900, 600) })
    {
      gallery.Relayout();
      gallery.ScrollTo(int.MaxValue);
      if (gallery.LogicalHeight <= 100_000 || gallery.ScrollOffset <= 32_767 ||
          !int.TryParse(gallery.ItemAt(new Point(100, 300))?.Id, out var visible) || visible < 250)
        return 59;
    }
    using (var pendingGallery = new VirtualBookshelfGallery(longShelf, 2, _ => { }, _ => { }, loadImages: false))
    {
      pendingGallery.ScrollTo(4200);
      pendingGallery.Size = new Size(900, 600);
      pendingGallery.Relayout();
      if (pendingGallery.ScrollOffset != 4200) return 60;
    }
    var directory = Path.Combine(Path.GetTempPath(), "omyaru-browser-self-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var path = Path.Combine(directory, "sync.json");
    try
    {
      BrowserStateStore.UseStateDirectoryForTests(directory);
      var oldState = Path.Combine(directory, "old-state");
      var newState = Path.Combine(directory, "new-state");
      Directory.CreateDirectory(newState);
      File.WriteAllText(Path.Combine(newState, "existing-data.marker"), "preserve");
      Directory.CreateDirectory(Path.Combine(oldState, "bookshelf"));
      File.WriteAllText(Path.Combine(oldState, "desktop-state.json"), "{\"deviceId\":\"migration-test\"}");
      File.WriteAllText(Path.Combine(oldState, "bookshelf-location.json"),
          JsonSerializer.Serialize(new { storageDirectory = Path.Combine(oldState, "bookshelf") }));
      File.WriteAllText(Path.Combine(oldState, "bookshelf", "catalog.json"),
          JsonSerializer.Serialize(new BookshelfCatalog { Items = [new BookshelfItem("a", BookshelfStore.DefaultFolder,
              Path.Combine(oldState, "bookshelf", "a.png"), "", 1, 1, 1)] }, BookshelfSyncReplica.Json));
      BrowserDataMigration.Move(oldState, newState);
      if (Directory.Exists(oldState) || !File.Exists(Path.Combine(newState, "desktop-state.json"))) return 34;
      if (Directory.GetDirectories(directory, "new-state.previous-*.backup").Length != 1 ||
          !File.Exists(Path.Combine(Directory.GetDirectories(directory, "new-state.previous-*.backup")[0], "existing-data.marker"))) return 38;
      var movedCatalog = JsonSerializer.Deserialize<BookshelfCatalog>(File.ReadAllText(Path.Combine(newState, "bookshelf", "catalog.json")), BookshelfSyncReplica.Json);
      if (movedCatalog?.Items.Single().FilePath != Path.Combine(newState, "bookshelf", "a.png")) return 35;
      using (var movedLocation = JsonDocument.Parse(File.ReadAllText(Path.Combine(newState, "bookshelf-location.json"))))
        if (movedLocation.RootElement.GetProperty("storageDirectory").GetString() != Path.Combine(newState, "bookshelf")) return 36;
      BrowserHistory.Record("https://example.com/history", "History test");
      BrowserHistory.Merge([new BrowserHistoryEntry("mobile-history", "https://example.com/mobile", "Mobile", 1700000000000, 2)]);
      if (BrowserHistory.Load().Count != 2) return 37;
      GoogleDriveDialogSelfTest.Run();
      DriveTransportSelfTest.RunAsync().GetAwaiter().GetResult();
      DriveReplicaSelfTest.Run();
      var snapshot = SyncSnapshot.CreateDefault();
      snapshot.Groups.Add(new TabGroupEntry("reading", "default", "Reading"));
      using (var preview = new Bitmap(320, 180))
      using (var graphics = Graphics.FromImage(preview))
      {
        graphics.Clear(Color.FromArgb(32, 38, 45));
        graphics.FillRectangle(Brushes.CornflowerBlue, 12, 12, 120, 70);
        if (!DesktopThumbnailStore.Save("article", preview)) return 10;
      }
      var thumbnail = DesktopThumbnailStore.ExportBase64("article");
      if (string.IsNullOrWhiteSpace(thumbnail) || DesktopThumbnailStore.Load("article") is not Image loadedPreview) return 11;
      loadedPreview.Dispose();
      snapshot.Tabs.Add(new TabEntry("article", "Article", "https://example.com", "default", "reading", 1, thumbnail));
      SyncCodec.Write(path, snapshot, "correct horse");
      var restored = SyncCodec.Read(path, "correct horse");
      if (!restored.Groups.Any(group => group.Id == "reading") ||
          !restored.Tabs.Any(tab => tab.Id == "article" && tab.GroupId == "reading" && tab.ThumbnailBase64 == thumbnail) ||
          restored.SchemaVersion != 2) return 2;

      var ordered = SyncSnapshot.CreateDefault();
      ordered.Tabs.Clear();
      ordered.Tabs.Add(new TabEntry("later", "Later", "https://example.com/later", "default", null, 1));
      ordered.Tabs.Add(new TabEntry("earlier", "Earlier", "https://example.com/earlier", "default", null, 0));
      SyncCodec.Write(path, ordered, "correct horse");
      var orderedRestored = SyncCodec.Read(path, "correct horse");
      if (orderedRestored.Tabs.Select(tab => tab.Id).SequenceEqual(["earlier", "later"]) == false ||
          orderedRestored.Tabs.Select(tab => tab.Order).SequenceEqual([0, 1]) == false) return 12;
      SyncCodec.Write(path, restored, "correct horse");
      try
      {
        SyncCodec.Read(path, "wrong password");
        return 3;
      }
      catch (System.Security.Cryptography.AuthenticationTagMismatchException)
      {
        // Expected: AES-GCM must authenticate the passphrase and payload.
      }
      if (AutoSyncDecisionEngine.Decide("same", "same", 10, 10) != AutoSyncDecision.Idle ||
          AutoSyncDecisionEngine.Decide("same", "same", 10, 11) != AutoSyncDecision.Export ||
          AutoSyncDecisionEngine.Decide("before", "after", 10, 10) != AutoSyncDecision.Import ||
          AutoSyncDecisionEngine.Decide("before", "after", 10, 11) != AutoSyncDecision.Conflict ||
          AutoSyncDecisionEngine.Decide(null, "after", 10, 11) != AutoSyncDecision.NotReady) return 4;

      var baseline = restored.Copy();
      baseline.ModifiedAtEpochMs = 100;
      SyncCodec.Write(path, baseline, "correct horse");
      AutoSyncSettingsStore.Configure(
          path,
          "correct horse",
          SyncFileFingerprint.Compute(path),
          baseline.ModifiedAtEpochMs);
      if (DesktopAutoSync.Run(baseline).Decision != AutoSyncDecision.Idle) return 5;

      var localChange = baseline.Copy();
      localChange.ModifiedAtEpochMs = 101;
      localChange.Tabs.Add(new TabEntry("local-auto", "Local", "https://example.com/local", "default", null));
      if (DesktopAutoSync.Run(localChange).Decision != AutoSyncDecision.Export ||
          !SyncCodec.Read(path, "correct horse").Tabs.Any(tab => tab.Id == "local-auto")) return 6;

      var remoteChange = localChange.Copy();
      remoteChange.ModifiedAtEpochMs = 102;
      remoteChange.Tabs.Add(new TabEntry("remote-auto", "Remote", "https://example.com/remote", "default", null));
      SyncCodec.Write(path, remoteChange, "correct horse");
      if (DesktopAutoSync.Run(localChange).Decision != AutoSyncDecision.NotReady) return 7;
      var importedOutcome = DesktopAutoSync.Run(localChange);
      if (importedOutcome.Decision != AutoSyncDecision.Import ||
          importedOutcome.Imported?.Tabs.Any(tab => tab.Id == "remote-auto") != true) return 8;
      AutoSyncSettingsStore.UpdateBaseline(
          AutoSyncSettingsStore.Load(),
          importedOutcome.Fingerprint!,
          importedOutcome.Imported.ModifiedAtEpochMs);

      var concurrentLocal = importedOutcome.Imported.Copy();
      concurrentLocal.ModifiedAtEpochMs = 103;
      var concurrentRemote = importedOutcome.Imported.Copy();
      concurrentRemote.ModifiedAtEpochMs = 104;
      concurrentRemote.Tabs.Add(new TabEntry("remote-conflict", "Conflict", "https://example.com/conflict", "default", null));
      SyncCodec.Write(path, concurrentRemote, "correct horse");
      if (DesktopAutoSync.Run(concurrentLocal).Decision != AutoSyncDecision.Conflict) return 9;

      var preferences = new DesktopPreferences
      {
        SearchEngine = "Bing",
        ShowFavoritesBar = false,
        BookmarkIconsOnly = true,
        CompactToolbar = true,
        AutomaticHttps = false,
        CollectDiagnostics = true,
        AudioOutputDeviceId = "test-device-id",
        AudioOutputLabel = "USB Speakers",
        LastCaptureDirectory = directory,
      };
      DesktopPreferencesStore.Save(preferences);
      var restoredPreferences = DesktopPreferencesStore.Load();
      if (restoredPreferences.SearchEngine != "Bing" || restoredPreferences.ShowFavoritesBar ||
          !restoredPreferences.BookmarkIconsOnly ||
          !restoredPreferences.CompactToolbar || restoredPreferences.AutomaticHttps ||
          !restoredPreferences.CollectDiagnostics || restoredPreferences.AudioOutputDeviceId != "test-device-id" ||
          restoredPreferences.AudioOutputLabel != "USB Speakers" ||
          restoredPreferences.LastCaptureDirectory != directory) return 13;

      var missingState = Path.Combine(directory, "missing-bookshelf-state");
      Directory.CreateDirectory(missingState);
      var missingRoot = Path.Combine(directory, "unavailable-bookshelf-drive");
      File.WriteAllText(Path.Combine(missingState, "bookshelf-location.json"),
          JsonSerializer.Serialize(new { storageDirectory = missingRoot }));
      BrowserStateStore.UseStateDirectoryForTests(missingState);
      if (!BookshelfStore.ConfiguredCatalogUnavailable) return 35;
      try { BookshelfStore.RecoverUnavailableStorage(Path.Combine(directory, "no-catalog")); return 36; }
      catch (IOException) { }
      BookshelfStore.RecoverUnavailableStorage(BookshelfStore.DefaultStorageDirectory);
      if (BookshelfStore.ConfiguredCatalogUnavailable ||
          !BookshelfStore.StorageDirectory.Equals(Path.Combine(missingState, "bookshelf"), StringComparison.OrdinalIgnoreCase) ||
          !Directory.EnumerateFiles(missingState, "bookshelf-location.json.unavailable-*.bak").Any()) return 37;
      BrowserStateStore.UseStateDirectoryForTests(directory);

      var shelf = BookshelfStore.Load();
      var shelfName = BookshelfStore.AddFolder(shelf, "Reference");
      shelf.FolderNotes[shelfName] = "Per-shelf notes remain editable and persistent.";
      BookshelfStore.Save(shelf);
      using (var capture = new Bitmap(240, 480))
      using (var graphics = Graphics.FromImage(capture))
      {
        graphics.Clear(Color.DarkSlateBlue);
        var shelfItem = BookshelfStore.AddImage(shelf, capture, "https://example.com/capture");
        if (shelfName != "Reference" || shelfItem.Width != 240 || shelfItem.Height != 480 ||
            !File.Exists(shelfItem.FilePath) || Path.GetFileName(shelfItem.FilePath) != "image1.png") return 14;
      }
      // Verify BookshelfForm scrolls to the bottom when a new image is pasted/added.
      for (var extra = 0; extra < 12; extra++)
      {
        using var tall = new Bitmap(300, 600);
        BookshelfStore.AddImage(shelf, tall, $"https://example.com/extra-{extra}");
      }
      var previousSyncContext = SynchronizationContext.Current;
      SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
      try
      {
        using var shelfForm = new BookshelfForm { ShowInTaskbar = false, Opacity = 0, StartPosition = FormStartPosition.Manual, Location = new Point(-32_000, -32_000) };
        shelfForm.Show();
        Application.DoEvents();
        shelfForm.ScrollGalleryForTest(0);
        Application.DoEvents();
        using var pasted = new Bitmap(300, 600);
        var pasteTask = shelfForm.PasteBitmapForTestAsync(pasted);
        var pasteWait = System.Diagnostics.Stopwatch.StartNew();
        while (!pasteTask.IsCompleted && pasteWait.ElapsedMilliseconds < 5000) Application.DoEvents();
        pasteTask.GetAwaiter().GetResult();
        Application.DoEvents();
        if (shelfForm.GalleryScrollOffsetForTest.Y <= 100) return 61;
        if (!shelfForm.VerifyColumnChangeAndFullscreenLayoutForTest(1, new Size(1920, 1080))) return 62;
        shelfForm.Close();
      }
      finally
      {
        SynchronizationContext.SetSynchronizationContext(previousSyncContext);
      }
      // Restore clean 1-item shelf state for the remaining storage/PDF/removal assertions below.
      shelf = BookshelfStore.Load();
      foreach (var extraItem in shelf.Items.Where(i => i.Folder == shelfName).Skip(1).ToArray())
        BookshelfStore.RemoveImage(shelf, extraItem);
      BookshelfStore.SetColumnCount(shelf, shelfName, 1);
      if (BookshelfStore.GetColumnCount(BookshelfStore.Load(), shelfName) != 1) return 41;
      var restoredShelf = BookshelfStore.Load();
      if (restoredShelf.ActiveFolder != "Reference" ||
          restoredShelf.FolderNotes.GetValueOrDefault("Reference") != "Per-shelf notes remain editable and persistent." ||
          restoredShelf.Items.Count(item => item.Folder == "Reference") != 1) return 15;
      var originalShelfImagePath = restoredShelf.Items.Single(item => item.Folder == "Reference").FilePath;
      var relocatedShelfDirectory = Path.Combine(directory, "relocated-bookshelf");
      BookshelfStore.MoveStorage(restoredShelf, relocatedShelfDirectory);
      if (!BookshelfStore.StorageDirectory.Equals(Path.GetFullPath(relocatedShelfDirectory), StringComparison.OrdinalIgnoreCase) ||
          File.Exists(originalShelfImagePath) ||
          restoredShelf.Items.Any(item => !File.Exists(item.FilePath)) ||
          BookshelfStore.Load().FolderNotes.GetValueOrDefault("Reference") !=
          "Per-shelf notes remain editable and persistent.") return 15;
      var pdfPath = Path.Combine(directory, "bookshelf-self-test.pdf");
      BookshelfPdfExporter.Export(restoredShelf, "Reference", pdfPath);
      if (!File.Exists(pdfPath) || new FileInfo(pdfPath).Length < 1000 ||
          Encoding.ASCII.GetString(File.ReadAllBytes(pdfPath), 0, 8) != "%PDF-1.4") return 16;
      var landscapePdfPath = Path.Combine(directory, "bookshelf-self-test-landscape.pdf");
      BookshelfPdfExporter.Export(
          restoredShelf,
          "Reference",
          landscapePdfPath,
          BookshelfPdfOrientation.Landscape);
      var landscapePdf = Encoding.ASCII.GetString(File.ReadAllBytes(landscapePdfPath));
      if (!landscapePdf.Contains("/MediaBox [0 0 960 540]", StringComparison.Ordinal)) return 17;
      var removable = restoredShelf.Items.Single(item => item.Folder == "Reference");
      var removablePath = removable.FilePath;
      using (var source = File.OpenRead(removablePath))
      {
        var manifest = new BookshelfImageChunks.Manifest(1, source.Length,
            Convert.ToHexString(SHA256.HashData(source)).ToLowerInvariant(), []);
        if (!DriveBookshelfSync.VerifiedOriginal(removablePath, manifest) ||
            DriveBookshelfSync.VerifiedOriginal(removablePath, manifest with { Sha256 = new string('0', 64) })) return 39;
      }
      if (!BookshelfStore.RemoveImage(restoredShelf, removable) || File.Exists(removablePath) ||
          BookshelfStore.Load().Items.Any(item => item.Id == removable.Id)) return 18;
      if (!BookshelfStore.ExplicitEmptyCatalog) return 40;
      BookshelfStore.ClearExplicitEmptyMarker();
      if (BookshelfStore.ExplicitEmptyCatalog) return 41;
      var folderId = Guid.NewGuid().ToString("N");
      var imageId = Guid.NewGuid().ToString("N");
      var remoteShelf = new JsonObject {
        ["folders"] = new JsonArray(new JsonObject { ["id"] = folderId, ["name"] = "Cloud", ["note"] = "", ["columns"] = 2 }),
        ["images"] = new JsonArray(new JsonObject { ["id"] = imageId, ["folderId"] = folderId,
            ["manifest"] = new JsonObject(), ["sourceUrl"] = "", ["width"] = 1, ["height"] = 1,
            ["createdAt"] = 1L, ["order"] = 0 }),
      };
      var remoteReplica = new BookshelfSyncReplica();
      remoteReplica.Capture(remoteShelf);
      remoteReplica.Data.Pending.Clear();
      var emptyShelf = new JsonObject { ["folders"] = new JsonArray(), ["images"] = new JsonArray() };
      if (!DriveBookshelfSync.ShouldRestoreStaleEmptyCatalog(remoteReplica, emptyShelf, true) ||
          DriveBookshelfSync.ShouldRestoreStaleEmptyCatalog(remoteReplica, emptyShelf, false)) return 42;

      var orderedFolders = new List<string> { "Omyaru Browser/동아리", "Omyaru Browser/얌전한 고양이",
          "Omyaru Browser/이세계 밀프헌터", "Omyaru Browser/동아리/1화" };
      var movedFolders = BookshelfPath.MoveSibling(orderedFolders, "Omyaru Browser/동아리", 1);
      if (movedFolders is null || !movedFolders.SequenceEqual(new[] { "Omyaru Browser/얌전한 고양이",
          "Omyaru Browser/동아리", "Omyaru Browser/동아리/1화", "Omyaru Browser/이세계 밀프헌터" })) return 43;
      var combinedShelf = new BookshelfCatalog { Folders = orderedFolders, Items =
      [
        new("one", orderedFolders[0], "", "", 1, 1, 1),
        new("two", orderedFolders[1], "", "", 1, 1, 2),
        new("three", orderedFolders[2], "", "", 1, 1, 3),
        new("four", orderedFolders[3], "", "", 1, 1, 4),
      ] };
      if (!BookshelfStore.VisibleItems(combinedShelf, "Omyaru Browser").Select(item => item.Id)
          .SequenceEqual(new[] { "one", "four", "two", "three" })) return 44;
      BookshelfStore.SetColumnCount(combinedShelf, "Omyaru Browser", 4);
      if (BookshelfStore.GetColumnCount(combinedShelf, "Omyaru Browser") != 4) return 45;

      var bookmarks = new BookmarkCatalog
      {
        Items =
        [
          new BookmarkEntry("first", "Example", "example.com", "E", Color.Blue.ToArgb()),
          new BookmarkEntry("second", "Docs", "https://example.com/docs", "D", Color.Green.ToArgb()),
        ],
      };
      BookmarkStore.Save(bookmarks);
      var restoredBookmarks = BookmarkStore.Load();
      if (restoredBookmarks.Items.Select(item => item.Id).SequenceEqual(["first", "second"]) == false ||
          restoredBookmarks.Items[0].Url != "https://example.com") return 19;
      using (var faviconSource = new Bitmap(48, 32))
      using (var graphics = Graphics.FromImage(faviconSource))
      using (var faviconPayload = new MemoryStream())
      {
        graphics.Clear(Color.Transparent);
        graphics.FillEllipse(Brushes.CornflowerBlue, 4, 0, 32, 32);
        faviconSource.Save(faviconPayload, System.Drawing.Imaging.ImageFormat.Png);
        faviconPayload.Position = 0;
        if (!BookmarkFaviconStore.SaveAsync("https://www.example.com/page", faviconPayload).GetAwaiter().GetResult())
          return 20;
      }
      using var restoredFavicon = BookmarkFaviconStore.LoadOrCreate(restoredBookmarks.Items[0]);
      if (restoredFavicon.Size != new Size(32, 32) ||
          !BookmarkFaviconStore.SameSite("https://example.com", "https://www.example.com/docs")) return 21;

      // Verify hierarchical bookmark folders, deduplication, deletion tombstones, and Netscape HTML round-trip.
      var bmCatalog = new BookmarkCatalog();
      var rootFolder = BookmarkStore.CreateFolder(bmCatalog, "기타 북마크");
      var subFolder = BookmarkStore.CreateFolder(bmCatalog, "aNG", rootFolder.Id);
      var leafFolder = BookmarkStore.CreateFolder(bmCatalog, "261013", subFolder.Id);
      BookmarkStore.AddOrUpdate(bmCatalog, "YouTube", "https://www.youtube.com", leafFolder.Id);
      BookmarkStore.AddOrUpdate(bmCatalog, "YouTube Mobile", "https://m.youtube.com/", leafFolder.Id);
      BookmarkStore.AddOrUpdate(bmCatalog, "GitHub", "https://github.com", null);
      BookmarkStore.Save(bmCatalog);
      var reloadedBm = BookmarkStore.Load();
      if (reloadedBm.Folders.Count != 3 || reloadedBm.Items.Count != 2) return 48;
      var removedItem = reloadedBm.Items.First(b => b.Url.Contains("github.com", StringComparison.OrdinalIgnoreCase));
      BookmarkStore.Remove(reloadedBm, removedItem);
      BookmarkStore.Save(reloadedBm);
      var htmlExportPath = Path.Combine(directory, "bookmarks-export.html");
      BookmarkStore.ExportHtml(reloadedBm, htmlExportPath);
      var importedCatalog = new BookmarkCatalog();
      var (impB, impF) = BookmarkStore.ImportHtml(importedCatalog, htmlExportPath);
      if (impF != 3 || impB != 1 || BookmarkStore.RecursiveBookmarkCount(importedCatalog, importedCatalog.Folders[0].Id) != 1)
        return 49;

      foreach (var safariIcon in Enum.GetValues<Safari.SafariIcon>())
      {
        var iconView = Safari.SafariIcons.Create(safariIcon);
        if (iconView is not System.Windows.Controls.Viewbox vb || vb.Child is not System.Windows.Controls.Canvas canvas || canvas.Children.Count == 0)
          return 46;
      }
      var testResources = new System.Windows.ResourceDictionary();
      Safari.SafariTheme.ApplyMacScrollBarStyles(testResources);
      if (!testResources.Contains(typeof(System.Windows.Controls.Primitives.ScrollBar)))
        return 47;

      using (var testForm = new Form { Size = new Size(320, 200), Opacity = 0, ShowInTaskbar = false })
      {
        WinFormsSurface.Attach(testForm, cloakFirstShow: false);
        var _ = testForm.Handle;
      }

      return 0;
    }
    finally
    {
      try { Directory.Delete(directory, recursive: true); } catch { }
    }
  }
}
