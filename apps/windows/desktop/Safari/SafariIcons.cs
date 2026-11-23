using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using PathShape = System.Windows.Shapes.Path;

namespace PrivacyBrowser.Desktop.Safari;

internal enum SafariIcon
{
  Sidebar,
  ChevronLeft,
  ChevronRight,
  ChevronDown,
  ChevronUp,
  PageMenu,
  StarOutline,
  StarFilled,
  Reload,
  Close,
  Sync,
  Extensions,
  Capture,
  WatermarkSparkles,
  Audio,
  Bookshelf,
  Plus,
  Minus,
  TabOverview,
  Ellipsis,
  Profile,
  Private,
  TabGroup,
  Search,
  HistoryClock,
  Globe,
  Reader,
  Distraction,
  Duplicate,
  Trash,
  Settings,
  Link,
  Copy,
  Save,
  Camera,
  Check,
  Undo,
  OpenExternal,
  WindowMinimize,
  WindowMaximize,
  WindowRestore,
  WindowClose,
  Folder,
  FolderFilled,
  FolderPlus,
  Sort,
  MoreVertical,
}

/// <summary>
/// SF Symbols-inspired crisp vector stroke icon factory (normalized 24x24 viewbox,
/// uniform round caps/joins, mathematically centered with zero font baseline drift).
/// </summary>
internal static class SafariIcons
{
  internal static UIElement Create(
      SafariIcon icon,
      double size = 15.5,
      Brush? brush = null,
      double strokeThickness = 1.65)
  {
    var fg = brush ?? SafariTheme.TextPrimary;
    var canvas = new Canvas
    {
      Width = 24,
      Height = 24,
      Background = Brushes.Transparent,
      IsHitTestVisible = false,
    };

    void Stroke(string data, double? thickness = null) => canvas.Children.Add(new PathShape
    {
      Data = Geometry.Parse(data),
      Stroke = fg,
      StrokeThickness = thickness ?? strokeThickness,
      StrokeStartLineCap = PenLineCap.Round,
      StrokeEndLineCap = PenLineCap.Round,
      StrokeLineJoin = PenLineJoin.Round,
      Fill = Brushes.Transparent,
    });

    void Fill(string data) => canvas.Children.Add(new PathShape
    {
      Data = Geometry.Parse(data),
      Fill = fg,
      Stroke = Brushes.Transparent,
    });

    void Circle(double cx, double cy, double r, bool filled = false, double? thickness = null)
    {
      var el = new Ellipse
      {
        Width = r * 2,
        Height = r * 2,
        Fill = filled ? fg : Brushes.Transparent,
        Stroke = filled ? Brushes.Transparent : fg,
        StrokeThickness = filled ? 0 : (thickness ?? strokeThickness),
      };
      Canvas.SetLeft(el, cx - r);
      Canvas.SetTop(el, cy - r);
      canvas.Children.Add(el);
    }

    switch (icon)
    {
      case SafariIcon.Sidebar:
        // macOS SF Symbol: sidebar.left
        Stroke("M 4.5,4.5 H 19.5 A 2.2,2.2 0 0 1 21.7,6.7 V 17.3 A 2.2,2.2 0 0 1 19.5,19.5 H 4.5 A 2.2,2.2 0 0 1 2.3,17.3 V 6.7 A 2.2,2.2 0 0 1 4.5,4.5 Z");
        Stroke("M 9.2,4.8 V 19.2");
        Stroke("M 4.8,8.5 H 6.8 M 4.8,11.8 H 6.8 M 4.8,15.1 H 6.3", 1.5);
        break;

      case SafariIcon.ChevronLeft:
        Stroke("M 14.8,5.5 L 8.3,12 L 14.8,18.5", 1.85);
        break;

      case SafariIcon.ChevronRight:
        Stroke("M 9.2,5.5 L 15.7,12 L 9.2,18.5", 1.85);
        break;

      case SafariIcon.ChevronDown:
        Stroke("M 6.5,9.5 L 12,15 L 17.5,9.5", 1.85);
        break;

      case SafariIcon.ChevronUp:
        Stroke("M 6.5,14.5 L 12,9 L 17.5,14.5", 1.85);
        break;

      case SafariIcon.PageMenu:
        // macOS 27 Safari address bar leading Page Menu glyph (crisp vertical separation)
        Stroke("M 4,5.6 A 1.8,1.8 0 0 1 5.8,3.8 H 18.2 A 1.8,1.8 0 0 1 20,5.6 V 10.6 A 1.8,1.8 0 0 1 18.2,12.4 H 5.8 A 1.8,1.8 0 0 1 4,10.6 Z", 1.55);
        Stroke("M 4.4,16.2 H 19.6 M 4.4,20.0 H 14.4", 1.6);
        break;

      case SafariIcon.StarOutline:
        Stroke("M 12,3.4 L 14.55,8.55 L 20.25,9.38 L 16.12,13.4 L 17.1,19.08 L 12,16.4 L 6.9,19.08 L 7.88,13.4 L 3.75,9.38 L 9.45,8.55 Z", 1.55);
        break;

      case SafariIcon.StarFilled:
        Fill("M 12,3.1 L 14.7,8.55 L 20.7,9.42 L 16.35,13.65 L 17.38,19.62 L 12,16.8 L 6.62,19.62 L 7.65,13.65 L 3.3,9.42 L 9.3,8.55 Z");
        break;

      case SafariIcon.Reload:
        // SF Symbol: arrow.clockwise
        Stroke("M 18.8,12.6 A 6.8,6.8 0 1 1 16.8,7.2");
        Stroke("M 16.8,3.6 V 7.5 H 12.9");
        break;

      case SafariIcon.Close:
        Stroke("M 6.5,6.5 L 17.5,17.5 M 17.5,6.5 L 6.5,17.5", 1.85);
        break;

      case SafariIcon.Sync:
        // SF Symbol: arrow.triangle.2.circlepath
        Stroke("M 18.7,9.5 A 7.1,7.1 0 0 0 6.8,7.1 L 5.2,8.6");
        Stroke("M 5.2,5.1 V 8.6 H 8.7");
        Stroke("M 5.3,14.5 A 7.1,7.1 0 0 0 17.2,16.9 L 18.8,15.4");
        Stroke("M 18.8,18.9 V 15.4 H 15.3");
        break;

      case SafariIcon.Extensions:
        // SF Symbol: puzzlepiece.extension (classic pinched-neck round nubs + left socket)
        Stroke("M 7.0,8.2 H 10.2 A 2.35,2.35 0 1 1 13.4,8.2 H 15.8 A 1.4,1.4 0 0 1 17.2,9.6 V 11.7 A 2.35,2.35 0 1 1 17.2,14.9 V 17.4 A 1.4,1.4 0 0 1 15.8,18.8 H 7.0 A 1.4,1.4 0 0 1 5.6,17.4 V 15.1 A 2.1,2.1 0 1 0 5.6,11.5 V 9.6 A 1.4,1.4 0 0 1 7.0,8.2 Z", 1.55);
        break;

      case SafariIcon.Capture:
        // SF Symbol: viewfinder
        Stroke("M 4,8.5 V 6 A 2,2 0 0 1 6,4 H 8.5 M 15.5,4 H 18 A 2,2 0 0 1 20,6 V 8.5 M 20,15.5 V 18 A 2,2 0 0 1 18,20 H 15.5 M 8.5,20 H 6 A 2,2 0 0 1 4,18 V 15.5");
        Circle(12, 12, 3.2, filled: false, thickness: 1.55);
        break;

      case SafariIcon.WatermarkSparkles:
        // Apple Intelligence-style crisp 4-point sparkles
        Stroke("M 11,3.5 C 11.4,8.1 13.4,10.1 18,10.5 C 13.4,10.9 11.4,12.9 11,17.5 C 10.6,12.9 8.6,10.9 4,10.5 C 8.6,10.1 10.6,8.1 11,3.5 Z", 1.5);
        Stroke("M 18.5,14.5 C 18.7,16.5 19.5,17.3 21.5,17.5 C 19.5,17.7 18.7,18.5 18.5,20.5 C 18.3,18.5 17.5,17.7 15.5,17.5 C 17.5,17.3 18.3,16.5 18.5,14.5 Z", 1.35);
        break;

      case SafariIcon.Audio:
        // SF Symbol: speaker.wave.2
        Stroke("M 4.5,9.5 H 8.2 L 13,5.5 V 18.5 L 8.2,14.5 H 4.5 A 1,1 0 0 1 3.5,13.5 V 10.5 A 1,1 0 0 1 4.5,9.5 Z", 1.55);
        Stroke("M 16.2,9.2 A 4.2,4.2 0 0 1 16.2,14.8", 1.55);
        Stroke("M 18.8,6.8 A 7.6,7.6 0 0 1 18.8,17.2", 1.55);
        break;

      case SafariIcon.Bookshelf:
        // SF Symbol: books.vertical (generous separation so all 3 books stay razor crisp)
        Stroke("M 3.6,4.2 H 5.6 A 0.9,0.9 0 0 1 6.5,5.1 V 18.9 A 0.9,0.9 0 0 1 5.6,19.8 H 3.6 A 0.9,0.9 0 0 1 2.7,18.9 V 5.1 A 0.9,0.9 0 0 1 3.6,4.2 Z", 1.5);
        Stroke("M 9.8,4.2 H 11.8 A 0.9,0.9 0 0 1 12.7,5.1 V 18.9 A 0.9,0.9 0 0 1 11.8,19.8 H 9.8 A 0.9,0.9 0 0 1 8.9,18.9 V 5.1 A 0.9,0.9 0 0 1 9.8,4.2 Z", 1.5);
        Stroke("M 15.4,5.6 L 17.5,5.0 A 0.9,0.9 0 0 1 18.6,5.7 L 21.3,18.2 A 0.9,0.9 0 0 1 20.6,19.3 L 18.5,19.9 A 0.9,0.9 0 0 1 17.4,19.2 L 14.7,6.7 A 0.9,0.9 0 0 1 15.4,5.6 Z", 1.5);
        break;

      case SafariIcon.Plus:
        Stroke("M 12,5.2 V 18.8 M 5.2,12 H 18.8", 1.8);
        break;

      case SafariIcon.Minus:
        Stroke("M 5.5,12 H 18.5", 1.8);
        break;

      case SafariIcon.TabOverview:
        // Exact macOS 27 Safari overlapping rounded squares (square.on.square)
        Stroke("M 3.8,10.6 A 2,2 0 0 1 5.8,8.6 H 13.4 A 2,2 0 0 1 15.4,10.6 V 18.2 A 2,2 0 0 1 13.4,20.2 H 5.8 A 2,2 0 0 1 3.8,18.2 Z", 1.55);
        Stroke("M 8.6,8.4 V 5.8 A 2,2 0 0 1 10.6,3.8 H 18.2 A 2,2 0 0 1 20.2,5.8 V 13.4 A 2,2 0 0 1 18.2,15.4 H 15.6", 1.55);
        break;

      case SafariIcon.Ellipsis:
        Circle(5.5, 12, 1.55, filled: true);
        Circle(12, 12, 1.55, filled: true);
        Circle(18.5, 12, 1.55, filled: true);
        break;

      case SafariIcon.Profile:
        Circle(12, 8.2, 3.4, filled: false, thickness: 1.65);
        Stroke("M 5.4,19.2 C 5.8,15.5 8.5,13.6 12,13.6 C 15.5,13.6 18.2,15.5 18.6,19.2", 1.65);
        break;

      case SafariIcon.Private:
        Stroke("M 3.5,11.5 H 20.5");
        Stroke("M 6.5,11.5 L 8.2,6.2 A 1.4,1.4 0 0 1 9.5,5.2 H 14.5 A 1.4,1.4 0 0 1 15.8,6.2 L 17.5,11.5", 1.55);
        Circle(8.4, 16.2, 2.5, filled: false, thickness: 1.55);
        Circle(15.6, 16.2, 2.5, filled: false, thickness: 1.55);
        Stroke("M 10.9,16.2 H 13.1", 1.55);
        break;

      case SafariIcon.TabGroup:
        Stroke("M 4.5,7.2 A 1.8,1.8 0 0 1 6.3,5.4 H 17.7 A 1.8,1.8 0 0 1 19.5,7.2 V 14.8 A 1.8,1.8 0 0 1 17.7,16.6 H 6.3 A 1.8,1.8 0 0 1 4.5,14.8 Z", 1.55);
        Stroke("M 7.5,19.6 H 16.5", 1.55);
        break;

      case SafariIcon.Search:
        Circle(10.8, 10.8, 5.8, filled: false, thickness: 1.65);
        Stroke("M 15.2,15.2 L 19.8,19.8", 1.8);
        break;

      case SafariIcon.HistoryClock:
        Circle(12, 12, 8.2, filled: false, thickness: 1.55);
        Stroke("M 12,7.6 V 12.2 L 15.2,14.2", 1.6);
        break;

      case SafariIcon.Globe:
        Circle(12, 12, 8.2, filled: false, thickness: 1.5);
        Stroke("M 4.2,12 H 19.8 M 12,3.8 C 14.8,6.5 14.8,17.5 12,20.2 C 9.2,17.5 9.2,6.5 12,3.8 Z", 1.35);
        break;

      case SafariIcon.Reader:
        Stroke("M 5,6.5 H 19 M 5,10.5 H 19 M 5,14.5 H 15 M 5,18.5 H 12", 1.65);
        break;

      case SafariIcon.Distraction:
        Stroke("M 5,19 L 15.2,8.8");
        Stroke("M 17,4.2 V 7.4 M 15.4,5.8 H 18.6", 1.5);
        Stroke("M 19.5,10.5 V 12.7 M 18.4,11.6 H 20.6", 1.35);
        Stroke("M 11,4.2 V 6.4 M 9.9,5.3 H 12.1", 1.35);
        break;

      case SafariIcon.Duplicate:
        Stroke("M 8.5,8.5 H 18.5 A 1.5,1.5 0 0 1 20,10 V 18.5 A 1.5,1.5 0 0 1 18.5,20 H 10 A 1.5,1.5 0 0 1 8.5,18.5 Z", 1.55);
        Stroke("M 15.5,8.5 V 5.5 A 1.5,1.5 0 0 0 14,4 H 5.5 A 1.5,1.5 0 0 0 4,5.5 V 14 A 1.5,1.5 0 0 0 5.5,15.5 H 8.5", 1.55);
        break;

      case SafariIcon.Trash:
        Stroke("M 5,7 H 19 M 9.2,7 V 5 A 1,1 0 0 1 10.2,4 H 13.8 A 1,1 0 0 1 14.8,5 V 7 M 6.8,7 L 7.6,18.5 A 1.5,1.5 0 0 0 9.1,19.9 H 14.9 A 1.5,1.5 0 0 0 16.4,18.5 L 17.2,7", 1.55);
        break;

      case SafariIcon.Settings:
        Circle(12, 12, 2.9, filled: false, thickness: 1.55);
        Stroke("M 12,3.4 V 5.7 M 12,18.3 V 20.6 M 3.4,12 H 5.7 M 18.3,12 H 20.6 M 5.9,5.9 L 7.6,7.6 M 16.4,16.4 L 18.1,18.1 M 18.1,5.9 L 16.4,7.6 M 7.6,16.4 L 5.9,18.1", 1.6);
        Circle(12, 12, 6.0, filled: false, thickness: 1.45);
        break;

      case SafariIcon.Link:
        Stroke("M 9.8,14.2 L 14.2,9.8 M 10.8,7.2 L 12.6,5.4 A 3.8,3.8 0 0 1 18,10.8 L 16.2,12.6 M 13.2,16.8 L 11.4,18.6 A 3.8,3.8 0 0 1 6,13.2 L 7.8,11.4", 1.6);
        break;

      case SafariIcon.Copy:
        Stroke("M 9,9 H 18 A 1.6,1.6 0 0 1 19.6,10.6 V 18 A 1.6,1.6 0 0 1 18,19.6 H 9 A 1.6,1.6 0 0 1 7.4,18 V 10.6 A 1.6,1.6 0 0 1 9,9 Z", 1.55);
        Stroke("M 15.5,9 V 6 A 1.6,1.6 0 0 0 13.9,4.4 H 6 A 1.6,1.6 0 0 0 4.4,6 V 14 A 1.6,1.6 0 0 0 6,15.6 H 7.4", 1.55);
        break;

      case SafariIcon.Save:
        Stroke("M 5.5,4.5 H 16.2 L 19.5,7.8 V 18.5 A 1.5,1.5 0 0 1 18,20 H 6 A 1.5,1.5 0 0 1 4.5,18.5 V 6 A 1.5,1.5 0 0 1 5.5,4.5 Z", 1.55);
        Stroke("M 8.5,4.8 V 9.5 H 15 V 4.8 M 8.5,19.6 V 14 H 15.5 V 19.6", 1.45);
        break;

      case SafariIcon.Camera:
        Stroke("M 4.5,8.5 H 7.4 L 9,6 H 15 L 16.6,8.5 H 19.5 A 1.5,1.5 0 0 1 21,10 V 18 A 1.5,1.5 0 0 1 19.5,19.5 H 4.5 A 1.5,1.5 0 0 1 3,18 V 10 A 1.5,1.5 0 0 1 4.5,8.5 Z", 1.55);
        Circle(12, 13.5, 3.1, filled: false, thickness: 1.55);
        break;

      case SafariIcon.Check:
        Stroke("M 5.5,12.4 L 10,16.8 L 18.8,7.6", 2.0);
        break;

      case SafariIcon.Undo:
        Stroke("M 7,9 H 14.5 A 4.8,4.8 0 1 1 14.5,18.6 H 8.5");
        Stroke("M 10,5.8 L 6.5,9 L 10,12.2");
        break;

      case SafariIcon.OpenExternal:
        Stroke("M 13.5,5.5 H 18.5 V 10.5 M 18.3,5.7 L 11,13 M 16.5,13.5 V 17.5 A 1.8,1.8 0 0 1 14.7,19.3 H 6.5 A 1.8,1.8 0 0 1 4.7,17.5 V 9.3 A 1.8,1.8 0 0 1 6.5,7.5 H 10.5", 1.55);
        break;

      case SafariIcon.WindowMinimize:
        Stroke("M 6,12 H 18", 1.55);
        break;

      case SafariIcon.WindowMaximize:
        Stroke("M 6.5,6.5 H 17.5 V 17.5 H 6.5 Z", 1.45);
        break;

      case SafariIcon.WindowRestore:
        Stroke("M 6.5,9 H 15 V 17.5 H 6.5 Z M 9.5,9 V 6.5 H 17.5 V 14.5 H 15", 1.4);
        break;

      case SafariIcon.WindowClose:
        Stroke("M 6.8,6.8 L 17.2,17.2 M 17.2,6.8 L 6.8,17.2", 1.55);
        break;

      case SafariIcon.Folder:
        Stroke("M 3.2,7.2 A 2,2 0 0 1 5.2,5.2 H 9.2 L 11.2,7.5 H 18.8 A 2,2 0 0 1 20.8,9.5 V 17.2 A 2,2 0 0 1 18.8,19.2 H 5.2 A 2,2 0 0 1 3.2,17.2 Z", 1.55);
        break;

      case SafariIcon.FolderFilled:
        Fill("M 3.2,7.0 A 2.1,2.1 0 0 1 5.3,4.9 H 9.3 L 11.4,7.3 H 18.7 A 2.1,2.1 0 0 1 20.8,9.4 V 17.1 A 2.1,2.1 0 0 1 18.7,19.2 H 5.3 A 2.1,2.1 0 0 1 3.2,17.1 Z");
        break;

      case SafariIcon.FolderPlus:
        Stroke("M 3.2,7.2 A 2,2 0 0 1 5.2,5.2 H 9.2 L 11.2,7.5 H 18.8 A 2,2 0 0 1 20.8,9.5 V 17.2 A 2,2 0 0 1 18.8,19.2 H 5.2 A 2,2 0 0 1 3.2,17.2 Z", 1.55);
        Stroke("M 12,10.6 V 16.0 M 9.3,13.3 H 14.7", 1.55);
        break;

      case SafariIcon.Sort:
        Stroke("M 4.5,7.2 H 19.5 M 7.2,12 H 16.8 M 10.2,16.8 H 13.8", 1.65);
        break;

      case SafariIcon.MoreVertical:
        Circle(12, 6.2, 1.5, filled: true);
        Circle(12, 12, 1.5, filled: true);
        Circle(12, 17.8, 1.5, filled: true);
        break;
    }

    return new Viewbox
    {
      Width = size,
      Height = size,
      Stretch = Stretch.Uniform,
      HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
      VerticalAlignment = VerticalAlignment.Center,
      IsHitTestVisible = false,
      Child = canvas,
    };
  }
}
