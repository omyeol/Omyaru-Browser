using System.Net;
using System.Text;

namespace PrivacyBrowser.Desktop;

internal static class DriveTransportSelfTest
{
  internal static async Task RunAsync()
  {
    var operation = Guid.Parse("12345678-1234-1234-1234-123456789abc");
    var name = DriveAppDataClient.Prefix + operation.ToString("N") + ".json";
    if (!DriveAppDataClient.ValidName(name) || DriveAppDataClient.ValidName("ordinary-file.json") ||
        DriveAppDataClient.ValidName(DriveAppDataClient.Prefix + new string('0', 32) + ".json"))
      throw new InvalidOperationException("Drive name validation failed.");
    if (!DriveAppDataClient.ValidUserDataName(DriveAppDataClient.UserDataPrefix + operation.ToString("N") + ".json") ||
        DriveAppDataClient.ValidUserDataName(DriveAppDataClient.UserDataPrefix + new string('0', 32) + ".json"))
      throw new InvalidOperationException("User data name validation failed.");
    var calls = 0;
    using var http = new HttpClient(new FakeHandler(async request =>
    {
      if (request.RequestUri?.Host != "www.googleapis.com" || request.Headers.Authorization?.Parameter != "test-token")
        throw new InvalidOperationException("Unexpected destination/authentication.");
      calls++;
      if (calls == 1) return Json("{\"nextPageToken\":\"page two\",\"files\":[{\"id\":\"one\",\"name\":\"" + name + "\"}]}");
      if (calls == 2)
      {
        if (!request.RequestUri.Query.Contains("pageToken=page%20two")) throw new InvalidOperationException("Missing pagination.");
        return Json("{\"files\":[{\"id\":\"other\",\"name\":\"unrelated.json\"}]}");
      }
      if (calls == 3)
      {
        var body = await request.Content!.ReadAsStringAsync();
        if (!body.Contains("appDataFolder") || !body.Contains(name) || !body.Contains("encrypted-test-payload"))
          throw new InvalidOperationException("Invalid app data upload.");
        return Json("{\"id\":\"uploaded\"}");
      }
      return new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("secret-server-response") };
    }));
    var client = new DriveAppDataClient(http, _ => Task.FromResult("test-token"));
    var entries = await client.ListAsync(CancellationToken.None);
    if (entries.Count != 1) throw new InvalidOperationException("App data filtering failed.");
    if (await client.AppendAsync(operation, "encrypted-test-payload", CancellationToken.None) != "uploaded")
      throw new InvalidOperationException("Upload failed.");
    try
    {
      await client.ReadAsync(entries[0], CancellationToken.None);
      throw new InvalidOperationException("Unauthorized response accepted.");
    }
    catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.Unauthorized)
    {
      if (error.Message.Contains("secret-server-response")) throw new InvalidOperationException("Sensitive body leaked.");
    }
    var snapshot = SyncSnapshot.CreateDefault();
    var encrypted = SyncCodec.Encrypt(snapshot, "test passphrase");
    if (SyncCodec.Decrypt(encrypted, "test passphrase").Tabs[0].Id != snapshot.Tabs[0].Id)
      throw new InvalidOperationException("In-memory encryption round trip failed.");

    // Verify re-authentication skips full tab-history scanning when account keys already exist.
    var keyGuid = Guid.Parse("abcdef01-2345-6789-abcd-ef0123456789");
    var keyId = keyGuid.ToString("N");
    var keySecret = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
    var keyRecord = DriveAccountKeys.Record(keyId, keySecret, null);
    var listedTabs = false;
    using var keyHttp = new HttpClient(new FakeHandler(request =>
    {
      var query = Uri.UnescapeDataString(request.RequestUri?.Query ?? "");
      if (query.Contains(DriveAppDataClient.KeyPrefix, StringComparison.Ordinal))
        return Task.FromResult(Json("{\"files\":[{\"id\":\"key-file-1\",\"name\":\"" + DriveAppDataClient.KeyPrefix + keyId + ".json\"}]}"));
      if (request.RequestUri?.AbsolutePath.EndsWith("/key-file-1", StringComparison.Ordinal) == true)
        return Task.FromResult(Json(keyRecord));
      if (query.Contains("omyaru-tabs-v", StringComparison.Ordinal))
      {
        listedTabs = true;
        return Task.FromResult(Json("{\"files\":[]}"));
      }
      throw new InvalidOperationException("Unexpected Drive call during existing-key reconnect: " + request.RequestUri);
    }));
    var keyClient = new DriveAppDataClient(keyHttp, _ => Task.FromResult("test-token")) { AccountMode = true };
    var ring = await DriveAccountKeys.ConnectAsync(keyClient, "", CancellationToken.None);
    if (listedTabs || ring.PrimaryKeyId != keyId)
      throw new InvalidOperationException("Existing account key reconnect must not scan tab history.");
  }

  private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
      { Content = new StringContent(body, Encoding.UTF8, "application/json") };
  private sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
  {
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => respond(request);
  }
}
