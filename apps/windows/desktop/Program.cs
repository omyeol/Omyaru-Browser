namespace PrivacyBrowser.Desktop;

internal static class Program
{
  [STAThread]
  private static int Main(string[] args)
  {
    if (args.Contains("--count-missing-shelf-originals", StringComparer.Ordinal))
      return Math.Min(200, BookshelfStore.MissingOriginalCount(BookshelfStore.Load()));
    if (args.Length == 2 && args[0] == "--report-shelf-access")
    {
      var catalog = BookshelfStore.Load();
      var first = catalog.Items.FirstOrDefault();
      File.WriteAllText(args[1], System.Text.Json.JsonSerializer.Serialize(new
      {
        StateDirectory = BrowserStateStore.StateDirectory,
        StorageDirectory = BookshelfStore.StorageDirectory,
        Items = catalog.Items.Count,
        Missing = BookshelfStore.MissingOriginalCount(catalog),
        FirstParent = first is null ? "" : Path.GetDirectoryName(first.FilePath),
        CatalogPresent = File.Exists(Path.Combine(BookshelfStore.StorageDirectory, "catalog.json")),
      }));
      return 0;
    }
    if (args.Contains("--check-cloud-shelf", StringComparer.Ordinal))
    {
      try { return CloudShelfMarker.CheckCurrentAccountReadOnly() ? 0 : 2; }
      catch { return 1; }
    }
    if (args.Contains("--count-shelf-remote-records", StringComparer.Ordinal))
    {
      try
      {
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        var client = new DriveAppDataClient(http, token => GoogleDriveAuthorization.AccessTokenAsync(token)) { AccountMode = true };
        return Math.Min(200, client.ListShelfAsync(CancellationToken.None).GetAwaiter().GetResult().Count);
      }
      catch { return 201; }
    }
    if (args.Length == 2 && args[0] == "--report-remote-shelf")
    {
      try
      {
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        var client = new DriveAppDataClient(http, token => GoogleDriveAuthorization.AccessTokenAsync(token)) { AccountMode = true };
        var keys = DriveAccountKeys.LoadAsync(client, CancellationToken.None).GetAwaiter().GetResult();
        var records = client.ListShelfAsync(CancellationToken.None).GetAwaiter().GetResult();
        var replica = new BookshelfSyncReplica();
        foreach (var record in records)
        {
          var batch = System.Text.Json.JsonSerializer.Deserialize<BookshelfSyncReplica.Batch>(
              keys.Decrypt(client.ReadAsync(record, CancellationToken.None).GetAwaiter().GetResult()), BookshelfSyncReplica.Json)!;
          replica.Apply(batch);
        }
        var snapshot = replica.Project();
        var folders = snapshot["folders"]!.AsArray();
        var target = folders.FirstOrDefault(folder => folder!["name"]!.GetValue<string>().EndsWith("/이세계 밀프헌터", StringComparison.Ordinal));
        var targetId = target?["id"]?.GetValue<string>();
        var images = snapshot["images"]!.AsArray();
        File.WriteAllText(args[1], System.Text.Json.JsonSerializer.Serialize(new
        {
          Records = records.Count, Folders = folders.Count, Images = images.Count,
          TargetFolderPresent = targetId is not null,
          TargetImages = images.Count(image => image!["folderId"]!.GetValue<string>() == targetId),
        }));
        return 0;
      }
      catch { return 1; }
    }
    if (args.Contains("--migrate-bookshelf-images", StringComparer.Ordinal))
    {
      var catalog = BookshelfStore.Load();
      if (BookshelfStore.ConfiguredCatalogUnavailable || BookshelfStore.MissingOriginalCount(catalog) > 0) return 2;
      BookshelfStore.TryMigrateImageNames(catalog);
      return BookshelfStore.HasCurrentImageNames(catalog) ? 0 : 3;
    }
    if (args.Contains("--self-test", StringComparer.Ordinal)) return DesktopSelfTest.Run();
    if (args.Length == 2 && args[0] == "--write-sync-fixture")
      return DesktopSelfTest.WriteFixture(args[1]);
    if (args.Contains("--migrate-only", StringComparer.Ordinal))
    {
      BrowserDataMigration.MoveLegacyState();
      return 0;
    }
    ApplicationConfiguration.Initialize();
    if (args.Contains("--smoke-test", StringComparer.Ordinal))
    {
      var shotIdx = Array.IndexOf(args, "--smoke-screenshot");
      var shotPath = shotIdx >= 0 && shotIdx + 1 < args.Length ? args[shotIdx + 1] : null;
      return DesktopSmokeTest.Run(shotPath);
    }
    if (args.Contains("--bookshelf-only", StringComparer.Ordinal))
    {
      Application.Run(new BookshelfForm());
      return 0;
    }
    try { BrowserDataMigration.MoveLegacyState(); }
    catch (Exception error)
    {
      MessageBox.Show("기존 Omyaru 데이터를 새 저장 위치로 옮기지 못했습니다. 실행 중인 Omyaru 창을 닫고 다시 시도해 주세요.\n\n" + error.Message,
          "Omyaru 데이터 이동", MessageBoxButtons.OK, MessageBoxIcon.Warning);
      return 1;
    }
    if (args.Contains("--legacy-winforms", StringComparer.Ordinal))
    {
      Application.Run(new BrowserShellForm());
      return 0;
    }
    var wpfApp = new System.Windows.Application
    {
      ShutdownMode = System.Windows.ShutdownMode.OnMainWindowClose,
    };
    // Required so modeless WinForms windows (e.g. BookshelfForm Ctrl+V/Ctrl+O/Esc) receive
    // PreProcessMessage / ProcessCmdKey keyboard routing under WPF's Dispatcher message pump.
    System.Windows.Forms.Integration.WindowsFormsHost.EnableWindowsFormsInterop();
    var mainWindow = new Safari.SafariBrowserWindow(enableAutomaticSync: true);
    wpfApp.Run(mainWindow);
    return 0;
  }

}
