namespace PrivacyBrowser.Desktop;

internal static class DesktopSmokeTest
{
  internal static int Run(string? screenshotPath = null)
  {
    // Never load or save the user's real browser state during an automated test.
    var testDirectory = Path.Combine(Path.GetTempPath(), "omyaru-smoke-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(testDirectory);
    BrowserStateStore.UseStateDirectoryForTests(testDirectory);
    var result = 4;
    var wpfApp = new System.Windows.Application
    {
      ShutdownMode = System.Windows.ShutdownMode.OnMainWindowClose,
    };
    var window = new Safari.SafariBrowserWindow(enableAutomaticSync: false)
    {
      Opacity = 0,
      ShowInTaskbar = false,
      WindowStartupLocation = System.Windows.WindowStartupLocation.Manual,
      Left = -32_000,
      Top = -32_000,
      WindowState = System.Windows.WindowState.Normal,
    };
    window.Loaded += async (_, _) =>
    {
      try
      {
        var layoutOk = window.HasValidSafariLayout;
        var settingsWindow = new Safari.SafariSettingsWindow(
            window,
            new DesktopPreferences(),
            _ => { },
            () => Task.CompletedTask,
            _ => Task.FromResult<string?>(null))
        {
          Opacity = 0,
          ShowInTaskbar = false,
          WindowStartupLocation = System.Windows.WindowStartupLocation.Manual,
          Left = -32_000,
          Top = -32_000,
        };
        settingsWindow.Show();
        settingsWindow.UpdateLayout();
        var settingsOk = settingsWindow.ActualWidth >= 600 && settingsWindow.ActualHeight >= 500;
        if (settingsOk && !string.IsNullOrWhiteSpace(screenshotPath) && settingsWindow.Content is System.Windows.FrameworkElement sRoot)
        {
          sRoot.UpdateLayout();
          var sw = Math.Max(1, (int)Math.Ceiling(sRoot.ActualWidth));
          var sh = Math.Max(1, (int)Math.Ceiling(sRoot.ActualHeight));
          var sRtb = new System.Windows.Media.Imaging.RenderTargetBitmap(sw, sh, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
          sRtb.Render(sRoot);
          var sEnc = new System.Windows.Media.Imaging.PngBitmapEncoder();
          sEnc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(sRtb));
          Directory.CreateDirectory(Path.GetDirectoryName(screenshotPath)!);
          using var sfs = File.Create(screenshotPath);
          sEnc.Save(sfs);
        }
        settingsWindow.Close();

        var demoCatalog = BookmarkStore.Load();
        var demoOther = BookmarkStore.CreateFolder(demoCatalog, "기타 북마크");
        var demoSub = BookmarkStore.CreateFolder(demoCatalog, "aNG", demoOther.Id);
        BookmarkStore.CreateFolder(demoCatalog, "261013", demoSub.Id);
        BookmarkStore.AddOrUpdate(demoCatalog, "GitHub", "https://github.com", demoSub.Id);
        var bmWindow = new Safari.SafariBookmarkManagerWindow(
            window,
            demoCatalog,
            _ => null,
            _ => { },
            _ => { })
        {
          Opacity = 0,
          ShowInTaskbar = false,
          WindowStartupLocation = System.Windows.WindowStartupLocation.Manual,
          Left = -32_000,
          Top = -32_000,
        };
        bmWindow.Show();
        bmWindow.UpdateLayout();
        var rootItems = demoCatalog.Items.Where(b => b.FolderId is null).Take(3).ToList();
        foreach (var item in rootItems) bmWindow.ToggleBookmarkSelection(item.Id);
        bmWindow.UpdateLayout();
        var multiSelectOk = bmWindow.SelectedBookmarkCountForTest == rootItems.Count;
        var bmOk = bmWindow.ActualWidth >= 800 && bmWindow.ActualHeight >= 540 && multiSelectOk;
        if (bmOk && !string.IsNullOrWhiteSpace(screenshotPath) && bmWindow.Content is System.Windows.FrameworkElement bmRoot)
        {
          bmRoot.UpdateLayout();
          var bw = Math.Max(1, (int)Math.Ceiling(bmRoot.ActualWidth));
          var bh = Math.Max(1, (int)Math.Ceiling(bmRoot.ActualHeight));
          var bRtb = new System.Windows.Media.Imaging.RenderTargetBitmap(bw, bh, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
          bRtb.Render(bmRoot);
          var bEnc = new System.Windows.Media.Imaging.PngBitmapEncoder();
          bEnc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bRtb));
          Directory.CreateDirectory(Path.GetDirectoryName(screenshotPath)!);
          using var bfs = File.Create(screenshotPath);
          bEnc.Save(bfs);
        }
        bmWindow.Close();

        var verifyTask = (layoutOk && settingsOk && bmOk) ? window.VerifySafariSmokeAsync() : Task.FromResult(false);
        var finished = await Task.WhenAny(verifyTask, Task.Delay(22_000)) == verifyTask;
        var smokeOk = finished && await verifyTask;
        result = !layoutOk ? 9 : !settingsOk ? 10 : !bmOk ? 13 : !finished ? 8 : smokeOk ? 0 : 11;
      }
      catch
      {
        result = 12;
      }
      finally
      {
        window.Close();
      }
    };
    wpfApp.Run(window);
    try { Directory.Delete(testDirectory, true); } catch { }
    return result;
  }
}
