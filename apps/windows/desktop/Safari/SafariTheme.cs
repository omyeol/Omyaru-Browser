using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using Point = System.Windows.Point;

namespace PrivacyBrowser.Desktop.Safari;

internal static class SafariTheme
{
  // macOS 27 Liquid Glass dark translucent palette
  internal static readonly Color WindowBgColor = Color.FromRgb(18, 19, 23);
  internal static readonly Color ToolbarBaseColor = Color.FromArgb(236, 24, 26, 32);
  internal static readonly Color SidebarBgColor = Color.FromArgb(242, 23, 25, 31);
  internal static readonly Color CapsuleFillColor = Color.FromArgb(196, 42, 45, 55);
  internal static readonly Color CapsuleHoverColor = Color.FromArgb(230, 58, 62, 75);
  internal static readonly Color CapsulePressedColor = Color.FromArgb(245, 72, 77, 94);
  internal static readonly Color CapsuleBorderColor = Color.FromArgb(46, 255, 255, 255);
  internal static readonly Color SpecularBorderColor = Color.FromArgb(68, 255, 255, 255);
  internal static readonly Color AddressFillColor = Color.FromArgb(214, 34, 37, 46);
  internal static readonly Color AddressFocusFillColor = Color.FromArgb(246, 28, 31, 39);
  internal static readonly Color ActiveTabFillColor = Color.FromArgb(235, 54, 58, 70);
  internal static readonly Color InactiveTabHoverColor = Color.FromArgb(145, 44, 47, 57);
  internal static readonly Color PopoverBgColor = Color.FromArgb(246, 30, 32, 39);
  internal static readonly Color CardFillColor = Color.FromArgb(208, 36, 39, 48);
  internal static readonly Color CardHoverColor = Color.FromArgb(238, 48, 52, 64);
  internal static readonly Color AccentColor = Color.FromRgb(10, 132, 255);
  internal static readonly Color AccentSoftColor = Color.FromArgb(52, 10, 132, 255);
  internal static readonly Color WarningAmberColor = Color.FromRgb(255, 192, 110);
  internal static readonly Color DangerRedColor = Color.FromRgb(255, 69, 58);
  internal static readonly Color SuccessGreenColor = Color.FromRgb(48, 209, 88);
  internal static readonly Color TextPrimaryColor = Color.FromRgb(245, 246, 250);
  internal static readonly Color TextSecondaryColor = Color.FromRgb(162, 167, 180);
  internal static readonly Color TextMutedColor = Color.FromRgb(118, 123, 138);
  internal static readonly Color DividerColor = Color.FromArgb(30, 255, 255, 255);

  internal static readonly SolidColorBrush WindowBg = Frozen(WindowBgColor);
  internal static readonly SolidColorBrush SidebarBg = Frozen(SidebarBgColor);
  internal static readonly SolidColorBrush CapsuleFill = Frozen(CapsuleFillColor);
  internal static readonly SolidColorBrush CapsuleHover = Frozen(CapsuleHoverColor);
  internal static readonly SolidColorBrush CapsuleBorder = Frozen(CapsuleBorderColor);
  internal static readonly SolidColorBrush SpecularBorder = Frozen(SpecularBorderColor);
  internal static readonly SolidColorBrush AddressFill = Frozen(AddressFillColor);
  internal static readonly SolidColorBrush AddressFocusFill = Frozen(AddressFocusFillColor);
  internal static readonly SolidColorBrush ActiveTabFill = Frozen(ActiveTabFillColor);
  internal static readonly SolidColorBrush PopoverBg = Frozen(PopoverBgColor);
  internal static readonly SolidColorBrush CardFill = Frozen(CardFillColor);
  internal static readonly SolidColorBrush Accent = Frozen(AccentColor);
  internal static readonly SolidColorBrush AccentSoft = Frozen(AccentSoftColor);
  internal static readonly SolidColorBrush WarningAmber = Frozen(WarningAmberColor);
  internal static readonly SolidColorBrush DangerRed = Frozen(DangerRedColor);
  internal static readonly SolidColorBrush SuccessGreen = Frozen(SuccessGreenColor);
  internal static readonly SolidColorBrush TextPrimary = Frozen(TextPrimaryColor);
  internal static readonly SolidColorBrush TextSecondary = Frozen(TextSecondaryColor);
  internal static readonly SolidColorBrush TextMuted = Frozen(TextMutedColor);
  internal static readonly SolidColorBrush Divider = Frozen(DividerColor);

  internal static readonly FontFamily UiFont = new("Segoe UI Variable Text, Segoe UI, Malgun Gothic");
  internal static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets, Segoe UI Symbol");

  internal static SolidColorBrush Frozen(Color color)
  {
    var b = new SolidColorBrush(color);
    b.Freeze();
    return b;
  }

  internal static Color FromArgbUint(uint argb) => Color.FromArgb(
      (byte)((argb >> 24) & 0xFF),
      (byte)((argb >> 16) & 0xFF),
      (byte)((argb >> 8) & 0xFF),
      (byte)(argb & 0xFF));

  internal static Color Blend(Color baseColor, Color tintColor, double weight)
  {
    weight = Math.Clamp(weight, 0, 1);
    return Color.FromArgb(
        baseColor.A,
        (byte)Math.Round(baseColor.R + (tintColor.R - baseColor.R) * weight),
        (byte)Math.Round(baseColor.G + (tintColor.G - baseColor.G) * weight),
        (byte)Math.Round(baseColor.B + (tintColor.B - baseColor.B) * weight));
  }

  internal static DropShadowEffect GlassShadow(double blur = 24, double opacity = 0.45, double depth = 8) => new()
  {
    Color = Colors.Black,
    BlurRadius = blur,
    Opacity = opacity,
    ShadowDepth = depth,
    Direction = 270,
  };

  internal static void AnimateColor(SolidColorBrush target, Color to, int ms = 160)
  {
    if (target.IsFrozen) return;
    target.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(to, TimeSpan.FromMilliseconds(ms))
    {
      EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
    });
  }

  internal static void AnimateDouble(UIElement target, DependencyProperty property, double to, int ms = 180, IEasingFunction? ease = null)
  {
    target.BeginAnimation(property, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms))
    {
      EasingFunction = ease ?? new CubicEase { EasingMode = EasingMode.EaseOut },
    });
  }

  internal static void AnimateScale(ScaleTransform transform, double to, int ms = 160)
  {
    var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms))
    {
      EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
    };
    transform.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
    transform.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
  }

  internal static Border CreateGlassButton(
      UIElement content,
      Action onClick,
      string tooltip,
      double width = 32,
      double height = 30,
      double cornerRadius = 15,
      bool transparentIdle = true,
      Color? accentFill = null)
  {
    var idleColor = accentFill ?? (transparentIdle ? Color.FromArgb(0, 58, 62, 75) : CapsuleFillColor);
    var hoverColor = accentFill is { } a
        ? Blend(a, Colors.White, 0.14)
        : CapsuleHoverColor;
    var pressColor = accentFill is { } ap
        ? Blend(ap, Colors.Black, 0.14)
        : CapsulePressedColor;

    var bg = new SolidColorBrush(idleColor);
    var scale = new ScaleTransform(1, 1);
    var host = new Grid
    {
      HorizontalAlignment = HorizontalAlignment.Stretch,
      VerticalAlignment = VerticalAlignment.Stretch,
      IsHitTestVisible = false,
    };
    host.Children.Add(content);
    var border = new Border
    {
      Width = double.IsNaN(width) ? double.NaN : width,
      Height = height,
      CornerRadius = new CornerRadius(cornerRadius),
      Background = bg,
      Child = host,
      Cursor = Cursors.Hand,
      ToolTip = tooltip,
      RenderTransformOrigin = new Point(0.5, 0.5),
      RenderTransform = scale,
      SnapsToDevicePixels = true,
    };

    border.MouseEnter += (_, _) => AnimateColor(bg, hoverColor, 110);
    border.MouseLeave += (_, _) =>
    {
      AnimateColor(bg, idleColor, 150);
      AnimateScale(scale, 1.0, 140);
    };
    border.MouseLeftButtonDown += (_, e) =>
    {
      e.Handled = true;
      AnimateColor(bg, pressColor, 60);
      AnimateScale(scale, 0.93, 70);
    };
    border.MouseLeftButtonUp += (_, e) =>
    {
      e.Handled = true;
      AnimateColor(bg, border.IsMouseOver ? hoverColor : idleColor, 120);
      AnimateScale(scale, 1.0, 140);
      onClick();
    };
    return border;
  }

  internal static Border CreateGlassCapsule(UIElement child, double height = 34, double radius = 17, Thickness? padding = null) => new()
  {
    Height = height,
    CornerRadius = new CornerRadius(radius),
    Background = new SolidColorBrush(CapsuleFillColor),
    BorderBrush = SpecularBorder,
    BorderThickness = new Thickness(1),
    Padding = padding ?? new Thickness(3, 0, 3, 0),
    Child = child,
    SnapsToDevicePixels = true,
  };

  internal static Border CreateCapsuleDivider() => new()
  {
    Width = 1,
    Height = 16,
    Background = Frozen(Color.FromArgb(36, 255, 255, 255)),
    Margin = new Thickness(3, 0, 3, 0),
    VerticalAlignment = VerticalAlignment.Center,
    IsHitTestVisible = false,
  };

  internal static void ApplyMacScrollBarStyles(ResourceDictionary resources)
  {
    const string scrollBarStyleXaml = """
<Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
       xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
       TargetType="{x:Type ScrollBar}">
  <Setter Property="Background" Value="Transparent"/>
  <Setter Property="Width" Value="10"/>
  <Setter Property="MinWidth" Value="10"/>
  <Setter Property="Template">
    <Setter.Value>
      <ControlTemplate TargetType="{x:Type ScrollBar}">
        <Grid Background="Transparent" SnapsToDevicePixels="True">
          <Track x:Name="PART_Track" IsDirectionReversed="True">
            <Track.DecreaseRepeatButton>
              <RepeatButton Command="ScrollBar.PageUpCommand" Focusable="False" Opacity="0"/>
            </Track.DecreaseRepeatButton>
            <Track.Thumb>
              <Thumb>
                <Thumb.Template>
                  <ControlTemplate TargetType="{x:Type Thumb}">
                    <Border x:Name="Pill" CornerRadius="3.5" Margin="2" Background="#40FFFFFF"/>
                    <ControlTemplate.Triggers>
                      <Trigger Property="IsMouseOver" Value="True">
                        <Setter TargetName="Pill" Property="Background" Value="#80FFFFFF"/>
                      </Trigger>
                      <Trigger Property="IsDragging" Value="True">
                        <Setter TargetName="Pill" Property="Background" Value="#A8FFFFFF"/>
                      </Trigger>
                    </ControlTemplate.Triggers>
                  </ControlTemplate>
                </Thumb.Template>
              </Thumb>
            </Track.Thumb>
            <Track.IncreaseRepeatButton>
              <RepeatButton Command="ScrollBar.PageDownCommand" Focusable="False" Opacity="0"/>
            </Track.IncreaseRepeatButton>
          </Track>
        </Grid>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
  <Style.Triggers>
    <Trigger Property="Orientation" Value="Horizontal">
      <Setter Property="Width" Value="Auto"/>
      <Setter Property="MinWidth" Value="0"/>
      <Setter Property="Height" Value="10"/>
      <Setter Property="MinHeight" Value="10"/>
      <Setter Property="Template">
        <Setter.Value>
          <ControlTemplate TargetType="{x:Type ScrollBar}">
            <Grid Background="Transparent" SnapsToDevicePixels="True">
              <Track x:Name="PART_Track" IsDirectionReversed="False">
                <Track.DecreaseRepeatButton>
                  <RepeatButton Command="ScrollBar.PageLeftCommand" Focusable="False" Opacity="0"/>
                </Track.DecreaseRepeatButton>
                <Track.Thumb>
                  <Thumb>
                    <Thumb.Template>
                      <ControlTemplate TargetType="{x:Type Thumb}">
                        <Border x:Name="Pill" CornerRadius="3.5" Margin="2" Background="#40FFFFFF"/>
                        <ControlTemplate.Triggers>
                          <Trigger Property="IsMouseOver" Value="True">
                            <Setter TargetName="Pill" Property="Background" Value="#80FFFFFF"/>
                          </Trigger>
                          <Trigger Property="IsDragging" Value="True">
                            <Setter TargetName="Pill" Property="Background" Value="#A8FFFFFF"/>
                          </Trigger>
                        </ControlTemplate.Triggers>
                      </ControlTemplate>
                    </Thumb.Template>
                  </Thumb>
                </Track.Thumb>
                <Track.IncreaseRepeatButton>
                  <RepeatButton Command="ScrollBar.PageRightCommand" Focusable="False" Opacity="0"/>
                </Track.IncreaseRepeatButton>
              </Track>
            </Grid>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Trigger>
  </Style.Triggers>
</Style>
""";
    if (System.Windows.Markup.XamlReader.Parse(scrollBarStyleXaml) is Style sbStyle)
      resources[typeof(System.Windows.Controls.Primitives.ScrollBar)] = sbStyle;
  }

  internal static TextBlock CreateGlyph(string text, double size = 13.5, Brush? brush = null, bool useIconFont = false) => new()
  {
    Text = text,
    FontFamily = useIconFont ? IconFont : UiFont,
    FontSize = size,
    Foreground = brush ?? TextPrimary,
    HorizontalAlignment = HorizontalAlignment.Center,
    VerticalAlignment = VerticalAlignment.Center,
    TextAlignment = TextAlignment.Center,
  };

  internal static Border CreatePopoverShell(UIElement content, double minWidth = 240, double maxWidth = 360) => new()
  {
    MinWidth = minWidth,
    MaxWidth = maxWidth,
    Background = PopoverBg,
    BorderBrush = SpecularBorder,
    BorderThickness = new Thickness(1),
    CornerRadius = new CornerRadius(14),
    Padding = new Thickness(8),
    Margin = new Thickness(12, 4, 12, 16),
    Effect = GlassShadow(22, 0.55, 8),
    Child = content,
  };

  internal static Border CreatePopoverRow(
      SafariIcon icon,
      string label,
      Action onClick,
      string? shortcut = null,
      bool isSelected = false,
      bool isDestructive = false)
  {
    var fg = isDestructive ? DangerRed : TextPrimary;
    var iconEl = SafariIcons.Create(icon, 14.5, isSelected ? Accent : fg, 1.55);
    return CreatePopoverRowCore(iconEl, label, onClick, shortcut, isSelected, isDestructive);
  }

  internal static Border CreatePopoverRow(
      string icon,
      string label,
      Action onClick,
      string? shortcut = null,
      bool isSelected = false,
      bool isDestructive = false)
  {
    var fg = isDestructive ? DangerRed : TextPrimary;
    var glyph = new TextBlock
    {
      Text = icon,
      FontFamily = UiFont,
      FontSize = 13,
      Foreground = isSelected ? Accent : fg,
      VerticalAlignment = VerticalAlignment.Center,
      HorizontalAlignment = HorizontalAlignment.Center,
    };
    return CreatePopoverRowCore(glyph, label, onClick, shortcut, isSelected, isDestructive);
  }

  private static Border CreatePopoverRowCore(
      UIElement iconElement,
      string label,
      Action onClick,
      string? shortcut,
      bool isSelected,
      bool isDestructive)
  {
    var idle = isSelected ? AccentSoftColor : Color.FromArgb(0, 255, 255, 255);
    var hover = isSelected ? Color.FromArgb(84, 10, 132, 255) : Color.FromArgb(28, 255, 255, 255);
    var bg = new SolidColorBrush(idle);

    var grid = new Grid();
    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

    var fg = isDestructive ? DangerRed : TextPrimary;
    var text = new TextBlock
    {
      Text = label,
      FontFamily = UiFont,
      FontSize = 12.5,
      FontWeight = isSelected ? FontWeights.SemiBold : FontWeights.Normal,
      Foreground = fg,
      VerticalAlignment = VerticalAlignment.Center,
      TextTrimming = TextTrimming.CharacterEllipsis,
      Margin = new Thickness(4, 0, 8, 0),
    };
    Grid.SetColumn(iconElement, 0);
    Grid.SetColumn(text, 1);
    grid.Children.Add(iconElement);
    grid.Children.Add(text);

    if (!string.IsNullOrWhiteSpace(shortcut))
    {
      var sc = new TextBlock
      {
        Text = shortcut,
        FontFamily = UiFont,
        FontSize = 11,
        Foreground = TextMuted,
        VerticalAlignment = VerticalAlignment.Center,
      };
      Grid.SetColumn(sc, 2);
      grid.Children.Add(sc);
    }

    var row = new Border
    {
      Height = 32,
      CornerRadius = new CornerRadius(8),
      Background = bg,
      Padding = new Thickness(6, 0, 10, 0),
      Cursor = Cursors.Hand,
      Child = grid,
    };
    row.MouseEnter += (_, _) => AnimateColor(bg, hover, 90);
    row.MouseLeave += (_, _) => AnimateColor(bg, idle, 130);
    row.MouseLeftButtonUp += (_, e) =>
    {
      e.Handled = true;
      onClick();
    };
    return row;
  }

  internal static Border CreatePopoverDivider() => new()
  {
    Height = 1,
    Background = Divider,
    Margin = new Thickness(6, 5, 6, 5),
  };
}
