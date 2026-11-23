using System.Drawing.Drawing2D;

namespace PrivacyBrowser.Desktop;

// Win32 child windows cannot reliably span a very tall bookshelf. Keep the
// canvas viewport-sized and draw only visible entries at their logical offset.
internal sealed class VirtualBookshelfGallery : UserControl
{
  private sealed record Tile(BookshelfItem Item, Rectangle Bounds);
  private readonly VScrollBar _scroll = new() { Dock = DockStyle.Right };
  private readonly List<Tile> _tiles = [];
  private readonly Dictionary<string, Image> _images = new(StringComparer.Ordinal);
  private readonly HashSet<string> _loading = new(StringComparer.Ordinal);
  private readonly HashSet<string> _failed = new(StringComparer.Ordinal);
  private static readonly SemaphoreSlim Transfers = new(3, 3);
  private readonly Action<BookshelfItem> _open;
  private readonly Action<BookshelfItem> _delete;
  private readonly bool _loadImages;
  private readonly bool _canDelete;
  private int _totalHeight;
  private int _columns;
  private int _generation;
  private int _cachedColumnWidth;
  private int? _pendingScrollOffset;
  private readonly Color _surface = Color.FromArgb(44, 44, 48);
  private readonly ContextMenuStrip _contextMenu = new()
  {
    ShowImageMargin = false,
    BackColor = Color.FromArgb(36, 36, 40),
    ForeColor = Color.FromArgb(245, 245, 247),
    Renderer = new BookshelfMenuRenderer(),
  };
  private BookshelfItem? _contextMenuItem;

  internal VirtualBookshelfGallery(IReadOnlyList<BookshelfItem> items, int columns,
      Action<BookshelfItem> open, Action<BookshelfItem> delete, bool loadImages = true, bool canDelete = true)
  {
    _open = open;
    _delete = delete;
    _columns = Math.Max(1, columns);
    _loadImages = loadImages;
    _canDelete = canDelete;
    DoubleBuffered = true;
    BackColor = Color.FromArgb(18, 18, 20);
    Controls.Add(_scroll);
    _scroll.Scroll += (_, _) => { Invalidate(); QueueVisibleImages(); };
    Items = items;
    Resize += (_, _) => Relayout();
    MouseWheel += (_, e) =>
    {
      var lines = SystemInformation.MouseWheelScrollLines;
      var step = lines < 0 ? Math.Max(80, ClientSize.Height - 80) : Math.Max(40, lines * 56);
      ScrollTo(_scroll.Value - Math.Sign(e.Delta) * step);
    };
    MouseDoubleClick += (_, e) => { if (HitTest(e.Location) is { } item) _open(item); };
    _contextMenu.Items.Add("사진 열기", null, (_, _) =>
    {
      if (_contextMenuItem is not null) _open(_contextMenuItem);
    });
    var deleteItem = _contextMenu.Items.Add("사진 삭제", null, (_, _) =>
    {
      if (_contextMenuItem is not null) _delete(_contextMenuItem);
    });
    deleteItem.ForeColor = Color.FromArgb(255, 69, 58);
    _contextMenu.Opening += (_, e) =>
    {
      var clientPoint = PointToClient(Cursor.Position);
      var item = HitTest(clientPoint);
      if (item is null)
      {
        e.Cancel = true;
        return;
      }
      _contextMenuItem = item;
      deleteItem.Enabled = _canDelete;
    };
    ContextMenuStrip = _contextMenu;
  }

  internal IReadOnlyList<BookshelfItem> Items { get; }
  internal int ScrollOffset => _pendingScrollOffset ?? _scroll.Value;
  internal int LogicalHeight => _totalHeight;
  internal BookshelfItem? ItemAt(Point point) => HitTest(point);

  internal void ScrollTo(int offset)
  {
    var clampedTarget = Math.Max(0, offset);
    if (_totalHeight <= 0 || ClientSize.Width < 80 || ClientSize.Height < 1)
    {
      _pendingScrollOffset = clampedTarget;
      return;
    }
    _pendingScrollOffset = null;
    _scroll.Value = Math.Clamp(clampedTarget, 0, Math.Max(0, _scroll.Maximum - _scroll.LargeChange + 1));
    Invalidate();
    QueueVisibleImages();
  }

  internal void Relayout()
  {
    if (IsDisposed || ClientSize.Width < 80 || ClientSize.Height < 1) return;
    var previous = _pendingScrollOffset ?? _scroll.Value;
    _pendingScrollOffset = null;

    int BuildTiles(int contentWidth)
    {
      _tiles.Clear();
      var baseWidth = Math.Max(1, contentWidth / _columns);
      var remainder = Math.Max(0, contentWidth - baseWidth * _columns);
      var colLefts = new int[_columns];
      var colWidths = new int[_columns];
      var x = 0;
      for (var c = 0; c < _columns; c++)
      {
        colWidths[c] = baseWidth + (c < remainder ? 1 : 0);
        colLefts[c] = x;
        x += colWidths[c];
      }
      var heights = new int[_columns];
      foreach (var item in Items)
      {
        var column = 0;
        for (var i = 1; i < heights.Length; i++)
          if (heights[i] < heights[column]) column = i;
        var colW = colWidths[column];
        var height = Math.Max(1, (int)Math.Round(colW * (double)item.Height / Math.Max(1, item.Width)));
        _tiles.Add(new Tile(item, new Rectangle(colLefts[column], heights[column], colW, height)));
        heights[column] = checked(heights[column] + height);
      }
      return heights.Length > 0 ? heights.Max() : 0;
    }

    var fullWidth = Math.Max(1, ClientSize.Width);
    var totalWithoutScroll = BuildTiles(fullWidth);
    var needsScroll = totalWithoutScroll > ClientSize.Height;
    var finalContentWidth = needsScroll ? Math.Max(1, ClientSize.Width - _scroll.Width) : fullWidth;
    _totalHeight = needsScroll ? BuildTiles(finalContentWidth) : totalWithoutScroll;

    var newColWidth = Math.Max(1, finalContentWidth / _columns);
    if (_cachedColumnWidth > 0 && Math.Abs(newColWidth - _cachedColumnWidth) > 32)
    {
      // Reload sharper thumbnails when column width changes substantially (e.g. entering fullscreen).
      _generation++;
      _loading.Clear();
      _failed.Clear();
      foreach (var img in _images.Values) img.Dispose();
      _images.Clear();
    }
    _cachedColumnWidth = newColWidth;

    _scroll.LargeChange = Math.Max(1, ClientSize.Height);
    _scroll.SmallChange = 80;
    _scroll.Maximum = Math.Max(_scroll.LargeChange, _totalHeight) - 1;
    _scroll.Visible = needsScroll;
    ScrollTo(previous);
  }

  private BookshelfItem? HitTest(Point point)
  {
    if (point.X >= ClientSize.Width - (_scroll.Visible ? _scroll.Width : 0)) return null;
    var logical = new Point(point.X, point.Y + _scroll.Value);
    return _tiles.FirstOrDefault(tile => tile.Bounds.Contains(logical))?.Item;
  }

  protected override void OnPaint(PaintEventArgs e)
  {
    base.OnPaint(e);
    e.Graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
    var top = _scroll.Value;
    var bottom = top + ClientSize.Height;
    foreach (var tile in _tiles)
    {
      if (tile.Bounds.Bottom <= top || tile.Bounds.Top >= bottom) continue;
      var bounds = new Rectangle(tile.Bounds.X, tile.Bounds.Y - top, tile.Bounds.Width, tile.Bounds.Height);
      if (_images.TryGetValue(tile.Item.Id, out var image)) e.Graphics.DrawImage(image, bounds);
      else
      {
        using var brush = new SolidBrush(_surface);
        e.Graphics.FillRectangle(brush, bounds);
        var message = _failed.Contains(tile.Item.Id) ? "사진을 열 수 없음" : "사진 불러오는 중…";
        TextRenderer.DrawText(e.Graphics, message, Font, bounds, Color.Gainsboro,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
      }
    }
    QueueVisibleImages();
  }

  private void QueueVisibleImages()
  {
    if (IsDisposed || !_loadImages) return;
    var top = _scroll.Value - ClientSize.Height;
    var bottom = _scroll.Value + ClientSize.Height * 2;
    foreach (var tile in _tiles.Where(tile => tile.Bounds.Bottom > top && tile.Bounds.Top < bottom))
    {
      if (_images.ContainsKey(tile.Item.Id) || _loading.Contains(tile.Item.Id) || _failed.Contains(tile.Item.Id)) continue;
      _loading.Add(tile.Item.Id);
      _ = LoadAsync(tile.Item, Math.Min(tile.Bounds.Width, tile.Item.Width),
          Math.Min(tile.Bounds.Height, tile.Item.Height), _generation);
    }
    // Keep a bounded decoded cache while preserving files and the catalog.
    if (_images.Count <= 30) return;
    var keep = _tiles.Where(tile => tile.Bounds.Bottom > top && tile.Bounds.Top < bottom)
        .Select(tile => tile.Item.Id).ToHashSet(StringComparer.Ordinal);
    foreach (var id in _images.Keys.Where(id => !keep.Contains(id)).ToArray())
    {
      _images.Remove(id, out var image);
      image?.Dispose();
      if (_images.Count <= 24) break;
    }
  }

  private async Task LoadAsync(BookshelfItem item, int width, int height, int generation)
  {
    Image? image = null;
    try
    {
      await Transfers.WaitAsync();
      try { image = await Task.Run(() => BookshelfStore.LoadThumbnail(item, Math.Max(1, width), Math.Max(1, height))); }
      finally { Transfers.Release(); }
      if (IsDisposed || generation != _generation) { image?.Dispose(); return; }
      if (image is null) _failed.Add(item.Id);
      else _images[item.Id] = image;
      Invalidate();
    }
    catch
    {
      image?.Dispose();
      if (!IsDisposed && generation == _generation) { _failed.Add(item.Id); Invalidate(); }
    }
    finally { _loading.Remove(item.Id); }
  }

  protected override void Dispose(bool disposing)
  {
    if (disposing)
    {
      _generation++;
      _contextMenu.Dispose();
      foreach (var image in _images.Values) image.Dispose();
      _images.Clear();
    }
    base.Dispose(disposing);
  }
}
