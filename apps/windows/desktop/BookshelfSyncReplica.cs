using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PrivacyBrowser.Desktop;

// Immutable, per-field operations. Deletion wins permanently for an entity ID.
internal sealed class BookshelfSyncReplica
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
  }
  private static readonly Dictionary<string, string[]> Fields = new()
  {
    ["folders"] = ["name", "note", "columns", "order"],
    ["images"] = ["folderId", "manifest", "sourceUrl", "width", "height", "createdAt", "order"],
  };
  private static bool IdValid(string id) => Regex.IsMatch(id, "^[0-9a-f]{32}$") && id != new string('0', 32);
  internal State Data { get; }
  internal BookshelfSyncReplica(State? state = null) => Data = state ?? new State();

  private static void Validate(Change c)
  {
    if (!IdValid(c.Id) || !Fields.TryGetValue(c.Kind, out var fields) ||
        (c.Field != "$deleted" && !fields.Contains(c.Field))) throw new InvalidDataException("책장 변경 필드가 올바르지 않습니다.");
    if (c.Field == "$deleted") { if (c.Value?.GetValue<bool>() != true) throw new InvalidDataException("잘못된 삭제 기록입니다."); return; }
    if (c.Value is null) throw new InvalidDataException("책장 값이 비어 있습니다.");
    switch (c.Field)
    {
      case "name": if (string.IsNullOrWhiteSpace(c.Value.GetValue<string>()) || c.Value.GetValue<string>().Length > 512) throw new InvalidDataException("책장 이름이 올바르지 않습니다."); break;
      case "note": if (c.Value.GetValue<string>().Length > 4000) throw new InvalidDataException("책장 메모가 너무 깁니다."); break;
      case "sourceUrl": if (c.Value.GetValue<string>().Length > 8192) throw new InvalidDataException("출처가 너무 깁니다."); break;
      case "folderId": if (!IdValid(c.Value.GetValue<string>())) throw new InvalidDataException("책장 ID가 올바르지 않습니다."); break;
      case "columns": if (c.Value.GetValue<int>() is < 1 or > 6) throw new InvalidDataException("책장 열 수가 올바르지 않습니다."); break;
      case "width": case "height": if (c.Value.GetValue<int>() is < 1 or > 1000000) throw new InvalidDataException("이미지 크기가 올바르지 않습니다."); break;
      case "order": if (c.Value.GetValue<int>() is < 0 or > 1000000) throw new InvalidDataException("이미지 순서가 올바르지 않습니다."); break;
      case "createdAt": if (c.Value.GetValue<long>() is < 0 or > 9000000000000000) throw new InvalidDataException("이미지 시간이 올바르지 않습니다."); break;
      // The image-transfer adapter validates the signed chunk references and hashes before local application.
      case "manifest": if (c.Value is not JsonObject || c.Value.ToJsonString().Length > 131072) throw new InvalidDataException("이미지 목록이 올바르지 않습니다."); break;
    }
  }
  private static Dictionary<string, JsonNode?> Flatten(JsonObject snapshot)
  {
    var flat = new Dictionary<string, JsonNode?>();
    foreach (var (kind, fields) in Fields)
    {
      var entities = snapshot[kind]?.AsArray() ?? throw new InvalidDataException("책장 목록이 없습니다.");
      if (entities.Count > 100000) throw new InvalidDataException("책장 목록이 너무 큽니다.");
      for (var index = 0; index < entities.Count; index++)
      {
        var entity = entities[index];
        var id = entity!["id"]!.GetValue<string>();
        foreach (var field in fields)
        {
          var value = field == "order" && kind == "folders" && entity[field] is null
              ? JsonValue.Create(index) : entity[field]?.DeepClone();
          Validate(new(kind, id, field, value));
          if (!flat.TryAdd($"{kind}/{id}/{field}", value)) throw new InvalidDataException("중복된 책장 항목입니다.");
        }
      }
    }
    return flat;
  }
  internal void Capture(JsonObject snapshot)
  {
    var next = Flatten(snapshot);
    var changes = new List<Change>();
    foreach (var (key, value) in next)
      if (!Data.Baseline.TryGetValue(key, out var before) || !JsonNode.DeepEquals(before, value))
      { var p = key.Split('/'); changes.Add(new(p[0], p[1], p[2], value?.DeepClone())); }
    var currentEntities = next.Keys.Select(k => k[..k.LastIndexOf('/')]).ToHashSet();
    foreach (var entity in Data.Baseline.Keys.Select(k => k[..k.LastIndexOf('/')]).Distinct())
      if (!currentEntities.Contains(entity)) { var p = entity.Split('/'); changes.Add(new(p[0], p[1], "$deleted", JsonValue.Create(true))); }
    foreach (var chunk in changes.Chunk(16))
    {
      var batch = new Batch(1, Guid.NewGuid().ToString("N"), Data.Actor, checked(Data.Clock + 1), chunk.ToList());
      Apply(batch); Data.Pending.Add(batch);
    }
    Data.Baseline = next;
  }
  internal void Apply(Batch batch)
  {
    if (batch.Protocol != 1 || !IdValid(batch.Id) || !IdValid(batch.Actor) || batch.Clock is < 1 or > 9000000000000000 ||
        batch.Changes.Count is < 1 or > 256) throw new InvalidDataException("책장 변경 기록이 올바르지 않습니다.");
    foreach (var c in batch.Changes) Validate(c); // Validate the whole operation before mutation.
    foreach (var c in batch.Changes)
    {
      var key = $"{c.Kind}/{c.Id}/{c.Field}";
      if (!Data.Registers.TryGetValue(key, out var old) || batch.Clock > old.Clock ||
          batch.Clock == old.Clock && string.CompareOrdinal(batch.Actor, old.Actor) > 0)
        Data.Registers[key] = new(batch.Clock, batch.Actor, c.Value?.DeepClone());
    }
    Data.Clock = Math.Max(Data.Clock, batch.Clock);
  }
  internal JsonObject Project()
  {
    var snapshot = new JsonObject();
    foreach (var (kind, fields) in Fields)
    {
      var entities = new JsonArray();
      foreach (var group in Data.Registers.Where(x => x.Key.StartsWith(kind + "/", StringComparison.Ordinal)).GroupBy(x => x.Key.Split('/')[1]).OrderBy(g => g.Key, StringComparer.Ordinal))
      {
        if (Data.Registers.ContainsKey($"{kind}/{group.Key}/$deleted")) continue;
        var entity = new JsonObject { ["id"] = group.Key };
        foreach (var field in group) entity[field.Key.Split('/')[2]] = field.Value.Value?.DeepClone();
        if (kind == "folders" && entity["order"] is null) entity["order"] = entities.Count;
        if (fields.All(entity.ContainsKey)) entities.Add(entity);
      }
      snapshot[kind] = entities;
    }
    return snapshot;
  }
  internal void Acknowledge(JsonObject applied) => Data.Baseline = Flatten(applied);
}
