using System.Text.Json;

namespace PrivacyBrowser.Desktop;

internal static class DriveReplicaSelfTest
{
  internal static void Run()
  {
    var local = SyncSnapshot.CreateDefault();
    local.Tabs = [new("a", "A", "https://example.com/a", "default", null, 0),
                  new("b", "B", "https://example.com/b", "default", null, 1)];
    var a = new DriveTabReplica(new() { Actor = new string('a', 32) });
    var b = new DriveTabReplica(new() { Actor = new string('b', 32) });
    a.Capture(local);
    b.Apply(a.Data.Pending[0]);
    b.Acknowledge(local);
    var closed = local.Copy();
    closed.Tabs.RemoveAt(0);
    a.Capture(closed);
    var edited = local.Copy();
    edited.Tabs[0] = edited.Tabs[0] with { Title = "Offline title" };
    b.Capture(edited);
    a.Apply(b.Data.Pending[0]);
    b.Apply(a.Data.Pending[1]);
    var projectedA = a.Project(closed);
    var projectedB = b.Project(edited);
    if (!projectedA.Tabs.SequenceEqual(projectedB.Tabs) || projectedA.Tabs.Any(t => t.Id == "a"))
      throw new InvalidOperationException("Drive delete/update convergence failed.");
    b.Apply(a.Data.Pending[0]); // Replayed old create must never resurrect a closed tab.
    if (b.Project(edited).Tabs.Any(t => t.Id == "a")) throw new InvalidOperationException("Drive tombstone lost.");
    var compacted = a.Compact();
    var restoredCheckpoint = new DriveTabReplica();
    restoredCheckpoint.ApplyDocument(JsonSerializer.Serialize(compacted, DriveTabReplica.Json), compacted.Id);
    restoredCheckpoint.Apply(a.Data.Pending[0]);
    if (!restoredCheckpoint.Project(closed).Tabs.SequenceEqual(projectedA.Tabs))
      throw new InvalidOperationException("Checkpoint lost stamps/tombstones.");
    a.Acknowledge(projectedA);
    var pending = a.Data.Pending.Count;
    a.Capture(projectedA);
    if (a.Data.Pending.Count != pending) throw new InvalidOperationException("Remote changes echoed as local edits.");
    var saved = JsonSerializer.Serialize(a.Data, DriveTabReplica.Json);
    var loaded = new DriveTabReplica(JsonSerializer.Deserialize<DriveTabReplica.State>(saved, DriveTabReplica.Json));
    if (!loaded.Project(closed).Tabs.SequenceEqual(projectedA.Tabs)) throw new InvalidOperationException("Replica restart failed.");
    var reordering = local.Copy();
    reordering.Tabs.Reverse();
    var orderReplica = new DriveTabReplica();
    orderReplica.Capture(local);
    orderReplica.Capture(reordering);
    if (!orderReplica.Project(local).Tabs.Select(t => t.Id).SequenceEqual(["b", "a"]))
      throw new InvalidOperationException("Drive reorder failed.");
    var privateTabs = local.Copy();
    privateTabs.Tabs.Add(new("secret", "Secret", "https://private.example", "default", "__private_workspace__"));
    var privateReplica = new DriveTabReplica();
    privateReplica.Capture(privateTabs);
    if (JsonSerializer.Serialize(privateReplica.Data, DriveTabReplica.Json).Contains("private.example"))
      throw new InvalidOperationException("Private tab leaked.");
    var challenge = GoogleDriveAuthorization.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk");
    if (challenge != "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM") throw new InvalidOperationException("PKCE vector failed.");
    // Verify forward-compatible iOS profile order field is accepted cleanly.
    var iosReplica = new DriveTabReplica();
    var iosBatchId = Guid.NewGuid().ToString("N");
    var iosActorId = Guid.NewGuid().ToString("N");
    var iosBatchJson = JsonSerializer.Serialize(new
    {
      protocol = 1,
      id = iosBatchId,
      actor = iosActorId,
      clock = 1795000000000L,
      changes = new object[]
      {
        new { kind = "profiles", id = "default", field = "name", value = "기본" },
        new { kind = "profiles", id = "default", field = "icon", value = "🌐" },
        new { kind = "profiles", id = "default", field = "order", value = 0 },
      },
    });
    iosReplica.ApplyDocument(iosBatchJson, iosBatchId);

    // Verify syncing >30 tabs (e.g. 60 merged tabs across PC + mobile) succeeds without throwing.
    var many = SyncSnapshot.CreateDefault();
    many.Tabs = Enumerable.Range(0, 60)
        .Select(i => new TabEntry($"tab-{i}", $"Tab {i}", $"https://example.com/{i}", "default", null, i))
        .ToList();
    var manyReplica = new DriveTabReplica();
    manyReplica.Capture(many);
    var projectedMany = manyReplica.Project(SyncSnapshot.CreateDefault());
    if (projectedMany.Tabs.Count != 60)
      throw new InvalidOperationException("Synced >30 tabs must project without truncation.");
  }
}
