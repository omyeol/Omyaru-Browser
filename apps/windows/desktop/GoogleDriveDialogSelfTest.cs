using System.Diagnostics;

namespace PrivacyBrowser.Desktop;

internal static class GoogleDriveDialogSelfTest
{
  internal static void Run()
  {
    // A clean installation must offer account connection without any local settings file.
    if (!GoogleDriveOAuthConfiguration.HasBundledClient)
      throw new InvalidOperationException("The distributed app must contain its desktop Google client.");
    const string url = "https://accounts.google.com/o/oauth2/v2/auth?state=test&scope=a%20b&redirect_uri=http%3A%2F%2F127.0.0.1%3A1234%2F";
    var launch = GoogleDriveLoginBrowser.CreateStartInfo(@"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", url);
    if (launch.UseShellExecute || !launch.RedirectStandardInput || !launch.RedirectStandardOutput ||
        !launch.RedirectStandardError || launch.ArgumentList.Count != 2 ||
        launch.ArgumentList[0] != "--app=" + url || launch.ArgumentList[1] != "--window-size=520,740")
      throw new InvalidOperationException("Drive login must open Edge app mode with an intact OAuth URL.");
    var attempts = 0;
    using var dialog = new GoogleDriveSyncDialog(async (_, token) =>
    {
      attempts++;
      await Task.Delay(Timeout.Infinite, token);
    }) { ShowInTaskbar = false, Opacity = 0 };
    dialog.Show();
    var layout = dialog.Controls.OfType<TableLayoutPanel>().Single();
    var password = layout.Controls.OfType<TextBox>().Single();
    var connect = layout.Controls.OfType<FlowLayoutPanel>().Single().Controls.OfType<Button>().First();
    if (layout.Controls.OfType<FlowLayoutPanel>().Single().Controls.OfType<Button>()
        .Any(button => button.Text.Contains("설정", StringComparison.Ordinal)))
      throw new InvalidOperationException("Google connection must not ask users to select settings files.");
    if (!connect.Enabled) throw new InvalidOperationException("Google connection must not require a new sync password.");
    password.Text = "12345";
    if (!connect.Enabled) throw new InvalidOperationException("Migration password length must not disable Google login.");
    password.Text = "123456";
    if (!connect.Enabled) throw new InvalidOperationException("Valid Drive password must enable connect.");
    for (var retry = 0; retry < 2; retry++)
    {
      connect.PerformClick();
      if (!connect.Enabled || connect.Text != "연결 취소" || password.Enabled)
        throw new InvalidOperationException("Drive authorization must remain cancellable.");
      connect.PerformClick();
      var watch = Stopwatch.StartNew();
      while (!password.Enabled && watch.ElapsedMilliseconds < 2000) Application.DoEvents();
      if (!connect.Enabled || !password.Enabled || connect.Text != "Google 계정 연결")
        throw new InvalidOperationException("Cancelled Drive authorization must permit immediate retry.");
    }
    if (attempts != 2) throw new InvalidOperationException("Drive authorization retry was not started.");
    password.Clear();
    if (!connect.Enabled) throw new InvalidOperationException("Passwordless Google connection must remain available.");
    dialog.Close();

    // Verify expired connected state prioritizes the Re-login button without clipping and runs authorize cleanly.
    var prevExpired = GoogleDriveAuthorization.NeedsReauthentication;
    try
    {
      GoogleDriveAuthorization.Save(new GoogleDriveAuthorization.Session("expired-test-token", "", true, "0123456789abcdef0123456789abcdef"));
      GoogleDriveAuthorization.NeedsReauthentication = true;
      var reloginAttempts = 0;
      using var expiredDialog = new GoogleDriveSyncDialog(async (_, _) =>
      {
        reloginAttempts++;
        GoogleDriveAuthorization.NeedsReauthentication = false;
        await Task.CompletedTask;
      }) { ShowInTaskbar = false, Opacity = 0 };
      expiredDialog.Show();
      var expiredLayout = expiredDialog.Controls.OfType<TableLayoutPanel>().Single();
      var expiredButtons = expiredLayout.Controls.OfType<FlowLayoutPanel>().Single().Controls.OfType<Button>().ToArray();
      if (expiredButtons.Length < 2 || expiredButtons[0].Text != "Google 계정 다시 로그인" || !expiredButtons[0].Visible || !expiredButtons[0].Enabled)
        throw new InvalidOperationException("Expired Drive dialog must prioritize the Re-login button.");
      expiredButtons[0].PerformClick();
      var wait = Stopwatch.StartNew();
      while (!expiredDialog.IsDisposed && expiredDialog.Visible && wait.ElapsedMilliseconds < 2000) Application.DoEvents();
      if (reloginAttempts != 1 || GoogleDriveAuthorization.NeedsReauthentication)
        throw new InvalidOperationException("Expired Drive re-login must execute and clear expired state.");
    }
    finally
    {
      GoogleDriveAuthorization.NeedsReauthentication = prevExpired;
      var testSessionPath = Path.Combine(BrowserStateStore.StateDirectory, "google-drive-session.dat");
      if (File.Exists(testSessionPath)) File.Delete(testSessionPath);
    }
  }
}
