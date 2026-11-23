using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PrivacyBrowser.Desktop;

// The JSON protocol is shared with Android. Logical clocks avoid device clock skew.
// Each field has its own LWW register; closing an entity is a permanent tombstone.
internal sealed class DriveTabReplica
{
  internal static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
  internal sealed record Change(string Kind, string Id, string Field, JsonNode? Value);
  internal sealed record Batch(int Protocol, string Id, string Actor, long Clock, List<Change> Changes);
  internal sealed record Register(long Clock, string Actor, JsonNode? Value);
  internal sealed class State
  {
    public string Actor { get; set; } = Guid.NewGuid().ToString("N");
    public long Clock { get; set; }
    public Dictionary<string, Register> Registers { get; set; } = [];
    public Dictionary<string, JsonNode?> Baseline { get; set; } = [];
    public List<Batch> Pending { get; set; } = [];
    public HashSet<string> SeenFiles { get; set; } = [];
    public HashSet<string> TabResetIds { get; set; } = [];
    public bool NeedsTabClear { get; set; }
  }
  internal State Data { get; }
  internal DriveTabReplica(State? state = null) => Data = state ?? new State();
  private static readonly Regex Identifier = new("^[a-zA-Z0-9_-]{1,128}$", RegexOptions.CultureInvariant);
  private static readonly Dictionary<string, string[]> Fields = new()
  {
    ["profiles"] = ["name", "icon", "color"],
    ["groups"] = ["profileId", "name"],
    ["tabs"] = ["title", "url", "profileId", "groupId", "order"],
  };
  private static readonly Dictionary<string, string[]> OptionalFields = new()
  {
    ["profiles"] = ["order"],
  };

  private static Dictionary<string, JsonNode?> Flatten(SyncSnapshot snapshot)
  {
    var copy = snapshot.Copy(false);
    copy.Groups.RemoveAll(g => g.Id == "__private_workspace__");
    copy.Tabs = copy.Tabs.Where(t => t.GroupId != "__private_workspace__")
        .Select((t, index) => t with { Order = index }).ToList();
    var root = JsonSerializer.SerializeToNode(copy, Json)!.AsObject();
    var result = new Dictionary<string, JsonNode?>();
    foreach (var (kind, fields) in Fields)
      foreach (var entity in root[kind]!.AsArray())
      {
        var id = entity!["id"]!.GetValue<string>();
        if (!Identifier.IsMatch(id)) throw new InvalidDataException("동기화 ID 형식이 올바르지 않습니다.");
        foreach (var field in fields) result[$"{kind}/{id}/{field}"] = entity[field]?.DeepClone();
      }
    return result;
  }

  internal void Capture(SyncSnapshot local)
  {
    var next = Flatten(local);
    var changes = new List<Change>();
    foreach (var (key, value) in next)
      if (!Data.Baseline.TryGetValue(key, out var before) || !JsonNode.DeepEquals(before, value))
      {
        var parts = key.Split('/');
        changes.Add(new Change(parts[0], parts[1], parts[2], value?.DeepClone()));
      }
    foreach (var key in Data.Baseline.Keys.Select(k => k[..k.LastIndexOf('/')]).Distinct())
      if (!next.Keys.Any(k => k.StartsWith(key + "/", StringComparison.Ordinal)))
      {
        var parts = key.Split('/');
        changes.Add(new Change(parts[0], parts[1], "$deleted", JsonValue.Create(true)));
      }
    if (changes.Count > 0)
    {
      foreach (var chunk in changes.Chunk(4096))
      {
        var batch = new Batch(1, Guid.NewGuid().ToString("N"), Data.Actor, checked(Data.Clock + 1), chunk.ToList());
        Apply(batch);
        Data.Pending.Add(batch);
      }
    }
    Data.Baseline = next;
  }

  internal void Apply(Batch batch, bool includeTabs = true)
  {
    if (batch.Protocol != 1 || !Guid.TryParseExact(batch.Id, "N", out _) ||
        !Guid.TryParseExact(batch.Actor, "N", out _) || batch.Clock is < 1 or > 9_000_000_000_000_000 ||
        batch.Changes.Count is < 1 or > 4096) throw new InvalidDataException("지원하지 않는 Drive 변경 기록입니다.");
    // Validate the entire batch before mutating the replica.
    foreach (var change in batch.Changes)
    {
      if (!Fields.TryGetValue(change.Kind, out var fields) || !Identifier.IsMatch(change.Id) ||
          !(fields.Contains(change.Field) ||
            (OptionalFields.TryGetValue(change.Kind, out var optional) && optional.Contains(change.Field)) ||
            change.Field == "$deleted") ||
          change.Value is JsonArray or JsonObject || (change.Value?.ToJsonString().Length ?? 0) > 32768)
        throw new InvalidDataException("잘못된 Drive 변경 항목입니다.");
      if (change.Field == "$deleted" && change.Value?.ToJsonString() != "true")
        throw new InvalidDataException("삭제 기록을 되돌릴 수 없습니다.");
      if (change.Field == "order" && !(change.Value is JsonValue order && order.TryGetValue<int>(out var rank) && rank >= 0) ||
          change.Field == "color" && !(change.Value is JsonValue color && color.TryGetValue<uint>(out _)) ||
          change.Field is "name" or "icon" or "title" or "url" or "profileId" &&
              !(change.Value is JsonValue text && text.TryGetValue<string>(out _)) ||
          change.Field == "groupId" && change.Value is not null && !(change.Value is JsonValue group && group.TryGetValue<string>(out _)))
        throw new InvalidDataException("Drive 항목의 값 형식이 올바르지 않습니다.");
    }
    foreach (var change in batch.Changes)
    {
      var key = $"{change.Kind}/{change.Id}/{change.Field}";
      if (!includeTabs && change.Kind == "tabs") continue;
      if (!Data.Registers.TryGetValue(key, out var old) || batch.Clock > old.Clock ||
          (batch.Clock == old.Clock && string.CompareOrdinal(batch.Actor, old.Actor) > 0))
        Data.Registers[key] = new Register(batch.Clock, batch.Actor, change.Value?.DeepClone());
    }
    Data.Clock = Math.Max(Data.Clock, batch.Clock);
  }

  internal SyncSnapshot Project(SyncSnapshot local)
  {
    var root = JsonSerializer.SerializeToNode(local.Copy(false), Json)!.AsObject();
    foreach (var kind in Fields.Keys)
    {
      var entities = new JsonArray();
      foreach (var group in Data.Registers.Where(k => k.Key.StartsWith(kind + "/", StringComparison.Ordinal))
          .GroupBy(k => k.Key.Split('/')[1]).OrderBy(g => g.Key, StringComparer.Ordinal))
      {
        if (Data.Registers.ContainsKey($"{kind}/{group.Key}/$deleted")) continue;
        var entity = new JsonObject { ["id"] = group.Key };
        foreach (var field in group) entity[field.Key.Split('/')[2]] = field.Value.Value?.DeepClone();
        if (Fields[kind].Any(f => !entity.ContainsKey(f))) continue;
        entities.Add(entity);
      }
      if (kind == "tabs") entities = new JsonArray(entities.OrderBy(e => e!["order"]!.GetValue<int>())
          .ThenBy(e => e!["id"]!.GetValue<string>(), StringComparer.Ordinal).Select(e => e!.DeepClone()).ToArray());
      root[kind] = entities;
    }
    var projected = root.Deserialize<SyncSnapshot>(Json)!;
    // Do not silently drop synced tabs/profiles/groups when the high safety cap is exceeded.
    if (projected.Tabs.Count > SyncSnapshot.MaxTabs || projected.Profiles.Count > SyncSnapshot.MaxProfiles ||
        projected.Groups.GroupBy(g => g.ProfileId).Any(g => g.Count() > SyncSnapshot.MaxGroupsPerProfile))
      throw new InvalidOperationException(
          $"연동된 탭({projected.Tabs.Count}/{SyncSnapshot.MaxTabs}) 또는 프로필/그룹이 최대 한도를 초과했습니다. 기존 기기에서 정리해 주세요.");
    projected.Normalize();
    return projected;
  }

  internal void Acknowledge(SyncSnapshot applied) { Data.Baseline = Flatten(applied); Data.NeedsTabClear = false; }

  internal void ObserveResets(IEnumerable<string> resetIds)
  {
    var ids = resetIds.ToHashSet(StringComparer.Ordinal);
    if (ids.Any(id => !Guid.TryParseExact(id, "N", out var parsed) || parsed == Guid.Empty))
      throw new InvalidDataException("탭 초기화 기록 형식이 올바르지 않습니다.");
    if (!ids.Except(Data.TabResetIds).Any()) return;
    Data.TabResetIds.UnionWith(ids);
    Data.NeedsTabClear = true;
    foreach (var key in Data.Registers.Keys.Where(k => k.StartsWith("tabs/", StringComparison.Ordinal)).ToArray()) Data.Registers.Remove(key);
    foreach (var key in Data.Baseline.Keys.Where(k => k.StartsWith("tabs/", StringComparison.Ordinal)).ToArray()) Data.Baseline.Remove(key);
    Data.Pending = Data.Pending.Select(b => b with { Changes = b.Changes.Where(c => c.Kind != "tabs").ToList() })
      .Where(b => b.Changes.Count > 0).ToList();
    Data.SeenFiles.Clear();
  }
  internal string Stamp(string json)
  {
    var root = JsonNode.Parse(json)!.AsObject();
    root["tabResetIds"] = JsonSerializer.SerializeToNode(Data.TabResetIds.Order(StringComparer.Ordinal));
    return root.ToJsonString();
  }

  internal sealed record Checkpoint(int Protocol, string Id, List<Batch> Batches);
  internal Checkpoint Compact()
  {
    var batches = Data.Registers.GroupBy(k => (k.Value.Clock, k.Value.Actor)).SelectMany(group =>
        group.Chunk(4096).Select(chunk => new Batch(1, Guid.NewGuid().ToString("N"), group.Key.Actor, group.Key.Clock,
            chunk.Select(k => { var parts = k.Key.Split('/'); return new Change(parts[0], parts[1], parts[2], k.Value.Value?.DeepClone()); }).ToList()))).ToList();
    return new Checkpoint(2, Guid.NewGuid().ToString("N"), batches);
  }

  internal void ApplyDocument(string json, string expectedId)
  {
    using var document = JsonDocument.Parse(json);
    var root = document.RootElement;
    var resets = root.TryGetProperty("tabResetIds", out var resetIds)
      ? resetIds.Deserialize<HashSet<string>>(Json) ?? throw new InvalidDataException("탭 초기화 세대가 올바르지 않습니다.") : [];
    if (resets.Count > 1000 || resets.Any(id => !Guid.TryParseExact(id, "N", out var parsed) || parsed == Guid.Empty))
      throw new InvalidDataException("탭 초기화 세대가 올바르지 않습니다.");
    var includeTabs = Data.TabResetIds.IsSubsetOf(resets);
    if (root.GetProperty("id").GetString() != expectedId) throw new InvalidDataException("Drive 변경 기록 ID가 일치하지 않습니다.");
    if (root.GetProperty("protocol").GetInt32() == 1)
    {
      Apply(JsonSerializer.Deserialize<Batch>(json, Json)!, includeTabs);
      return;
    }
    if (root.GetProperty("protocol").GetInt32() != 2) throw new InvalidDataException("지원하지 않는 Drive 데이터입니다.");
    var checkpoint = JsonSerializer.Deserialize<Checkpoint>(json, Json)!;
    if (checkpoint.Batches.Count > 50000) throw new InvalidDataException("Drive 기록이 너무 큽니다.");
    // A checkpoint keeps original field stamps and every tombstone. Validate in a
    // temporary replica before accepting it; it is not a newer overwrite snapshot.
    var validated = new DriveTabReplica();
    foreach (var batch in checkpoint.Batches) validated.Apply(batch);
    foreach (var batch in checkpoint.Batches) Apply(batch, includeTabs);
  }
}
