namespace PrivacyBrowser.Desktop;

internal static class BookshelfPath
{
  internal static bool Contains(string parent, string path) => path.Equals(parent, StringComparison.OrdinalIgnoreCase) || path.StartsWith(parent + "/", StringComparison.OrdinalIgnoreCase);
  internal static string Parent(string path) => path.Contains('/') ? path[..path.LastIndexOf('/')] : "";
  internal static string Leaf(string path) => path[(path.LastIndexOf('/') + 1)..];
  internal static List<string> Children(IEnumerable<string> paths, string parent) => paths
      .Select(path => parent.Length == 0 ? path.Split('/')[0]
          : path.StartsWith(parent + "/", StringComparison.OrdinalIgnoreCase)
              ? parent + "/" + path[(parent.Length + 1)..].Split('/')[0] : null)
      .Where(path => path is not null).Select(path => path!)
      .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
  internal static List<string>? MoveSibling(List<string> paths, string folder, int direction)
  {
    var parent = Parent(folder);
    var siblings = Children(paths, parent);
    var index = siblings.FindIndex(path => path.Equals(folder, StringComparison.OrdinalIgnoreCase));
    var target = index + direction;
    if (index < 0 || target < 0 || target >= siblings.Count) return null;
    (siblings[index], siblings[target]) = (siblings[target], siblings[index]);
    var result = new List<string>();
    void Visit(string path)
    {
      var actual = paths.FirstOrDefault(item => item.Equals(path, StringComparison.OrdinalIgnoreCase));
      if (actual is not null) result.Add(actual);
      foreach (var child in path.Equals(parent, StringComparison.OrdinalIgnoreCase) ? siblings : Children(paths, path)) Visit(child);
    }
    foreach (var root in parent.Length == 0 ? siblings : Children(paths, "")) Visit(root);
    return result.Count == paths.Count ? result : null;
  }
  internal static string? Normalize(string value)
  {
    var path = value.Trim(); var parts = path.Split('/');
    var reserved = new HashSet<string>(new[] { "CON", "PRN", "AUX", "NUL" }.Concat(Enumerable.Range(1, 9).SelectMany(i => new[] { $"COM{i}", $"LPT{i}" })), StringComparer.OrdinalIgnoreCase);
    if (new[] { ".omyaru", ".omyaru-control" }.Contains(parts[0], StringComparer.OrdinalIgnoreCase) || path.Length > 512 || parts.Length > 16 || parts.Any(p => string.IsNullOrWhiteSpace(p) || p != p.Trim() || p is "." or ".." || p.Length > 100 || p.EndsWith('.') || p.Any(c => c < 32 || "\\:*?\"<>|".Contains(c)) || reserved.Contains(p.Split('.')[0]))) return null;
    return path;
  }
  internal static Dictionary<string, string>? Relocation(List<string> paths, string source, string destination)
  {
    var target = Normalize(destination);
    if (target is null || target.StartsWith(source + "/", StringComparison.OrdinalIgnoreCase)) return null;
    var moving = paths.Where(p => Contains(source, p)).ToList();
    if (moving.Count == 0) return null;
    var result = moving.ToDictionary(p => p, p => target + p[source.Length..], StringComparer.OrdinalIgnoreCase);
    if (result.Values.Any(p => Normalize(p) is null) || paths.Any(p => !moving.Contains(p) && result.Values.Any(d => Contains(d, p)))) return null;
    return result;
  }
}
