using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text;

namespace PrivacyBrowser.Desktop;

internal static class DesktopThumbnailStore
{
  private const int MaximumWidth = 640;
  private const int MaximumHeight = 420;
  private const int MaximumEncodedBytes = 512 * 1024;
  private const int MaximumBase64Chars = 700 * 1024;

  private static string DirectoryPath => Path.Combine(BrowserStateStore.StateDirectory, "tab-thumbnails");

  internal static bool Save(string tabId, Image image)
  {
    if (string.IsNullOrWhiteSpace(tabId) || image.Width <= 0 || image.Height <= 0) return false;
    try
    {
      Directory.CreateDirectory(DirectoryPath);
      using var thumbnail = Resize(image);
      var path = ThumbnailPath(tabId);
      var temporaryPath = path + ".new";
      EncodeJpeg(thumbnail, temporaryPath, 72L);
      if (new FileInfo(temporaryPath).Length > MaximumEncodedBytes)
        EncodeJpeg(thumbnail, temporaryPath, 52L);
      if (new FileInfo(temporaryPath).Length > MaximumEncodedBytes)
      {
        File.Delete(temporaryPath);
        return false;
      }
      File.Move(temporaryPath, path, true);
      return true;
    }
    catch
    {
      return false;
    }
  }

  internal static Image? SavePreview(string tabId, byte[] encodedImage)
  {
    if (string.IsNullOrWhiteSpace(tabId) || encodedImage.Length == 0) return null;
    try
    {
      using var stream = new MemoryStream(encodedImage, writable: false);
      using var decoded = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: false);
      using var thumbnail = Resize(decoded);
      Directory.CreateDirectory(DirectoryPath);
      var path = ThumbnailPath(tabId);
      var temporaryPath = path + ".new";
      EncodeJpeg(thumbnail, temporaryPath, 72L);
      if (new FileInfo(temporaryPath).Length > MaximumEncodedBytes)
        EncodeJpeg(thumbnail, temporaryPath, 52L);
      if (new FileInfo(temporaryPath).Length > MaximumEncodedBytes)
      {
        File.Delete(temporaryPath);
        return null;
      }
      File.Move(temporaryPath, path, true);
      return new Bitmap(thumbnail);
    }
    catch
    {
      return null;
    }
  }

  internal static Image? Load(string tabId)
  {
    try
    {
      var path = ThumbnailPath(tabId);
      var file = new FileInfo(path);
      if (!file.Exists || file.Length is <= 0 or > MaximumEncodedBytes) return null;
      using var stream = new MemoryStream(File.ReadAllBytes(path), writable: false);
      using var decoded = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: true);
      return new Bitmap(decoded);
    }
    catch
    {
      Delete(tabId);
      return null;
    }
  }

  internal static string? ExportBase64(string tabId)
  {
    try
    {
      var path = ThumbnailPath(tabId);
      var file = new FileInfo(path);
      if (!file.Exists || file.Length is <= 0 or > MaximumEncodedBytes) return null;
      return Convert.ToBase64String(File.ReadAllBytes(path));
    }
    catch
    {
      return null;
    }
  }

  internal static bool ImportBase64(string tabId, string? encoded)
  {
    if (string.IsNullOrWhiteSpace(encoded) || encoded.Length > MaximumBase64Chars) return false;
    try
    {
      var bytes = Convert.FromBase64String(encoded);
      if (bytes.Length is <= 0 or > MaximumEncodedBytes) return false;
      using var stream = new MemoryStream(bytes, writable: false);
      using var decoded = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: true);
      return Save(tabId, decoded);
    }
    catch
    {
      return false;
    }
  }

  internal static void Delete(string tabId)
  {
    try
    {
      var path = ThumbnailPath(tabId);
      File.Delete(path);
      File.Delete(path + ".new");
    }
    catch
    {
      // Cache deletion must never prevent a tab operation.
    }
  }

  internal static void Prune(IEnumerable<string> validTabIds)
  {
    try
    {
      if (!Directory.Exists(DirectoryPath)) return;
      var validNames = validTabIds.Select(tabId => Path.GetFileName(ThumbnailPath(tabId)))
          .ToHashSet(StringComparer.OrdinalIgnoreCase);
      foreach (var file in Directory.EnumerateFiles(DirectoryPath))
        if (!validNames.Contains(Path.GetFileName(file))) File.Delete(file);
    }
    catch
    {
      // Best-effort cache maintenance.
    }
  }

  private static Bitmap Resize(Image source)
  {
    var scale = Math.Min(1D, Math.Min((double)MaximumWidth / source.Width, (double)MaximumHeight / source.Height));
    var width = Math.Max(1, (int)Math.Round(source.Width * scale));
    var height = Math.Max(1, (int)Math.Round(source.Height * scale));
    var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
    using var graphics = Graphics.FromImage(bitmap);
    graphics.Clear(Color.FromArgb(31, 39, 47));
    graphics.CompositingQuality = CompositingQuality.HighQuality;
    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
    graphics.SmoothingMode = SmoothingMode.HighQuality;
    graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
    graphics.DrawImage(source, new Rectangle(0, 0, width, height));
    return bitmap;
  }

  private static void EncodeJpeg(Image image, string path, long quality)
  {
    var codec = ImageCodecInfo.GetImageEncoders().First(item => item.FormatID == ImageFormat.Jpeg.Guid);
    using var parameters = new EncoderParameters(1);
    parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
    image.Save(path, codec, parameters);
  }

  private static string ThumbnailPath(string tabId)
  {
    var digest = SHA256.HashData(Encoding.UTF8.GetBytes(tabId));
    return Path.Combine(DirectoryPath, Convert.ToHexString(digest).ToLowerInvariant() + ".jpg");
  }
}
