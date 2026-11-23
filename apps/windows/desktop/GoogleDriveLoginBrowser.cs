using System.Diagnostics;

namespace PrivacyBrowser.Desktop;

internal static class GoogleDriveLoginBrowser
{
  internal static ProcessStartInfo CreateStartInfo(string executable, string authorizationUrl)
  {
    // This is the real Edge browser, not a reparented or script-controlled WebView.
    // Keep the regular Edge profile so existing Google sessions remain available.
    // A login browser may print startup messages. Never let those messages share
    // the sync bridge's JSON stdout pipe (or inherit its command input pipe).
    var start = new ProcessStartInfo(executable)
    {
      UseShellExecute = false,
      RedirectStandardInput = true,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
    };
    start.ArgumentList.Add("--app=" + authorizationUrl);
    start.ArgumentList.Add("--window-size=520,740");
    return start;
  }

  internal static void Open(string authorizationUrl)
  {
    var executable = new[]
    {
      Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
      Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
      Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    }.Where(root => !string.IsNullOrWhiteSpace(root))
     .Select(root => Path.Combine(root, "Microsoft", "Edge", "Application", "msedge.exe"))
     .FirstOrDefault(File.Exists);
    if (executable is null)
    {
      try
      {
        Process.Start(new ProcessStartInfo(authorizationUrl) { UseShellExecute = true });
        return;
      }
      catch (Exception error)
      {
        throw new InvalidOperationException("로그인 브라우저를 열 수 없습니다. Microsoft Edge 또는 기본 브라우저를 확인해 주세요.", error);
      }
    }
    // Edge may hand off to an existing process: process exit does not mean the
    // login window closed. Do not cancel OAuth or kill the user's browser here.
    var process = Process.Start(CreateStartInfo(executable, authorizationUrl));
    if (process is null) throw new InvalidOperationException("Edge 로그인 창을 열지 못했습니다. 다시 시도해 주세요.");
    process.StandardInput.Close();
    _ = DrainAndReleaseAsync(process);
  }

  private static async Task DrainAndReleaseAsync(Process process)
  {
    try
    {
      await Task.WhenAll(
        process.StandardOutput.BaseStream.CopyToAsync(Stream.Null),
        process.StandardError.BaseStream.CopyToAsync(Stream.Null),
        process.WaitForExitAsync());
    }
    catch { /* Browser handoff/exit is not an OAuth cancellation. */ }
    finally { process.Dispose(); }
  }
}
