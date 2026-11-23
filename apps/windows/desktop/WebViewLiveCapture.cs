using System.Drawing.Imaging;
using System.Globalization;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace PrivacyBrowser.Desktop;

internal static class WebViewLiveCapture
{
  internal sealed record LiveCaptureSelection(
      double X,
      double Y,
      double Width,
      double Height,
      string Folder,
      CaptureAction Action);

  private sealed record CaptureViewportState(
      double ScrollX,
      double ScrollY,
      double ViewportWidth,
      double ViewportHeight);

  internal static async Task<LiveCaptureSelection?> SelectLiveCaptureRegionAsync(
      CoreWebView2? core,
      BookshelfCatalog catalog,
      CaptureAction fallbackAction,
      bool showBookshelfPicker)
  {
    if (core is null) return null;
    var token = Guid.NewGuid().ToString("N");
    var completion = new TaskCompletionSource<LiveCaptureSelection?>(TaskCreationOptions.RunContinuationsAsynchronously);
    var lensFrame = await CaptureLiveLensFrameAsync(core);
    var lensRefreshBusy = 0;

    async Task RefreshLensFrameAsync(double scrollX, double scrollY)
    {
      if (Interlocked.Exchange(ref lensRefreshBusy, 1) != 0) return;
      try
      {
        if (core is null || completion.Task.IsCompleted) return;
        await core.ExecuteScriptAsync($$"""
            window.__omyaruPrepareCaptureLensFrame?.({{JsonSerializer.Serialize(token)}});
            """);
        var frame = await CaptureLiveLensFrameAsync(core);
        if (string.IsNullOrWhiteSpace(frame) || core is null || completion.Task.IsCompleted) return;
        await core.ExecuteScriptAsync($$"""
            window.__omyaruSetCaptureLensFrame?.(
              {{JsonSerializer.Serialize(token)}},
              {{JsonSerializer.Serialize(frame)}},
              {{scrollX.ToString(CultureInfo.InvariantCulture)}},
              {{scrollY.ToString(CultureInfo.InvariantCulture)}});
            """);
      }
      catch
      {
        // A navigation or teardown can race a debounced lens refresh.
      }
      finally
      {
        try
        {
          if (core is not null && !completion.Task.IsCompleted)
            await core.ExecuteScriptAsync($$"""
                window.__omyaruRestoreCaptureLensFrame?.({{JsonSerializer.Serialize(token)}});
                """);
        }
        catch { }
        Interlocked.Exchange(ref lensRefreshBusy, 0);
      }
    }

    void Received(object? sender, CoreWebView2WebMessageReceivedEventArgs eventArgs)
    {
      try
      {
        using var message = JsonDocument.Parse(eventArgs.WebMessageAsJson);
        var root = message.RootElement;
        if (!root.TryGetProperty("channel", out var channel) || channel.GetString() != "omyaru-live-capture" ||
            !root.TryGetProperty("token", out var messageToken) || messageToken.GetString() != token ||
            !root.TryGetProperty("action", out var action)) return;
        switch (action.GetString())
        {
          case "cancel":
            completion.TrySetResult(null);
            break;
          case "refresh-lens":
            var scrollX = root.TryGetProperty("scrollX", out var scrollXValue) ? scrollXValue.GetDouble() : 0D;
            var scrollY = root.TryGetProperty("scrollY", out var scrollYValue) ? scrollYValue.GetDouble() : 0D;
            _ = RefreshLensFrameAsync(scrollX, scrollY);
            break;
          case "complete":
            var operation = root.TryGetProperty("operation", out var operationValue)
                ? operationValue.GetString()
                : null;
            var selectedAction = operation switch
            {
              "save" => CaptureAction.SavePng,
              "copy" => CaptureAction.Copy,
              "bookshelf" => CaptureAction.AddToBookshelf,
              _ => fallbackAction,
            };
            var selection = new LiveCaptureSelection(
                root.GetProperty("x").GetDouble(),
                root.GetProperty("y").GetDouble(),
                root.GetProperty("width").GetDouble(),
                root.GetProperty("height").GetDouble(),
                root.TryGetProperty("folder", out var folder) ? folder.GetString() ?? catalog.ActiveFolder : catalog.ActiveFolder,
                selectedAction);
            completion.TrySetResult(selection.Width >= 2 && selection.Height >= 2 ? selection : null);
            break;
        }
      }
      catch
      {
        // Ignore unrelated or incomplete page messages while the selector is active.
      }
    }

    void Navigating(object? sender, CoreWebView2NavigationStartingEventArgs eventArgs) => completion.TrySetResult(null);
    core.WebMessageReceived += Received;
    core.NavigationStarting += Navigating;
    try
    {
      await InjectLiveCaptureOverlayAsync(
          core,
          token,
          catalog.Folders,
          catalog.ActiveFolder,
          showBookshelfPicker,
          lensFrame);
      return await completion.Task;
    }
    finally
    {
      core.WebMessageReceived -= Received;
      core.NavigationStarting -= Navigating;
      await RemoveLiveCaptureOverlayAsync(core, token);
    }
  }

  internal static async Task<Bitmap?> CapturePageRegionBitmapAsync(CoreWebView2? core, LiveCaptureSelection selection)
  {
    if (core is null || selection.Width < 2 || selection.Height < 2) return null;
    try
    {
      var initialState = await GetCaptureViewportStateAsync(core);
      if (initialState is null) return null;
      await Task.Delay(24);

      using var initialFrame = await CaptureViewportBitmapAsync(core) ?? await CaptureViewportViaDevToolsAsync(core);
      if (initialFrame is null || initialFrame.Width < 2 || initialFrame.Height < 2)
      {
        return await CaptureDomMediaRegionAsync(core, selection);
      }

      var scaleX = initialFrame.Width / Math.Max(1D, initialState.ViewportWidth);
      var scaleY = initialFrame.Height / Math.Max(1D, initialState.ViewportHeight);

      var fitsInCurrentViewport =
          selection.X >= initialState.ScrollX - 1.5 &&
          selection.Y >= initialState.ScrollY - 1.5 &&
          selection.X + selection.Width <= initialState.ScrollX + initialState.ViewportWidth + 1.5 &&
          selection.Y + selection.Height <= initialState.ScrollY + initialState.ViewportHeight + 1.5;

      if (fitsInCurrentViewport)
      {
        var cropped = CropViewportBitmap(
            initialFrame,
            selection.X - initialState.ScrollX,
            selection.Y - initialState.ScrollY,
            selection.Width,
            selection.Height,
            scaleX,
            scaleY);
        if (IsNearlyUniformColor(cropped))
        {
          using var devToolsFrame = await CaptureViewportViaDevToolsAsync(core);
          if (devToolsFrame is not null)
          {
            var devScaleX = devToolsFrame.Width / Math.Max(1D, initialState.ViewportWidth);
            var devScaleY = devToolsFrame.Height / Math.Max(1D, initialState.ViewportHeight);
            var altCropped = CropViewportBitmap(
                devToolsFrame,
                selection.X - initialState.ScrollX,
                selection.Y - initialState.ScrollY,
                selection.Width,
                selection.Height,
                devScaleX,
                devScaleY);
            if (!IsNearlyUniformColor(altCropped))
            {
              cropped.Dispose();
              return altCropped;
            }
            altCropped.Dispose();
          }
          var domFallback = await CaptureDomMediaRegionAsync(core, selection);
          if (domFallback is not null && !IsNearlyUniformColor(domFallback))
          {
            cropped.Dispose();
            return domFallback;
          }
          domFallback?.Dispose();
        }
        return cropped;
      }

      var outWidth = Math.Max(2, (int)Math.Round(selection.Width * scaleX));
      var outHeight = Math.Max(2, (int)Math.Round(selection.Height * scaleY));
      var stitched = new Bitmap(outWidth, outHeight, PixelFormat.Format32bppPArgb);
      try
      {
        using var graphics = Graphics.FromImage(stitched);
        graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
        graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;

        var selectionBottom = selection.Y + selection.Height;
        var currentDocY = selection.Y;
        var targetScrollX = selection.X >= initialState.ScrollX &&
                            selection.X + selection.Width <= initialState.ScrollX + initialState.ViewportWidth
            ? initialState.ScrollX
            : Math.Max(0D, selection.X);
        var previousScrollY = double.NaN;

        for (var sliceIndex = 0; sliceIndex < 48 && currentDocY < selectionBottom - 0.5; sliceIndex++)
        {
          var targetScrollY = Math.Max(0D, currentDocY);
          var sliceState = await ScrollAndSettleForCaptureAsync(core, targetScrollX, targetScrollY);
          if (sliceState is null) break;
          await Task.Delay(28);

          using var sliceFrame = await CaptureBestViewportFrameAsync(core);
          if (sliceFrame is null || IsNearlyUniformColor(sliceFrame)) break;

          var sliceScaleX = sliceFrame.Width / Math.Max(1D, sliceState.ViewportWidth);
          var sliceScaleY = sliceFrame.Height / Math.Max(1D, sliceState.ViewportHeight);

          var cssLeft = Math.Max(0D, selection.X - sliceState.ScrollX);
          var cssTop = currentDocY - sliceState.ScrollY;
          if (cssTop < 0D)
          {
            currentDocY -= cssTop;
            cssTop = 0D;
          }
          var availableCssHeight = sliceState.ViewportHeight - cssTop;
          var takeCssHeight = Math.Min(selectionBottom - currentDocY, availableCssHeight);
          if (takeCssHeight <= 0.5) break;

          var destTopPx = Math.Clamp((int)Math.Round((currentDocY - selection.Y) * scaleY), 0, outHeight - 1);
          var destBottomPx = Math.Clamp((int)Math.Round((currentDocY + takeCssHeight - selection.Y) * scaleY), destTopPx + 1, outHeight);
          var destHeightPx = destBottomPx - destTopPx;

          var srcLeftPx = Math.Clamp((int)Math.Round(cssLeft * sliceScaleX), 0, Math.Max(0, sliceFrame.Width - 1));
          var srcTopPx = Math.Clamp((int)Math.Round(cssTop * sliceScaleY), 0, Math.Max(0, sliceFrame.Height - 1));
          var srcWidthPx = Math.Clamp((int)Math.Round(selection.Width * sliceScaleX), 1, Math.Max(1, sliceFrame.Width - srcLeftPx));
          var srcHeightPx = Math.Clamp(destHeightPx, 1, Math.Max(1, sliceFrame.Height - srcTopPx));

          graphics.DrawImage(
              sliceFrame,
              new Rectangle(0, destTopPx, outWidth, destHeightPx),
              new Rectangle(srcLeftPx, srcTopPx, srcWidthPx, srcHeightPx),
              GraphicsUnit.Pixel);

          currentDocY += takeCssHeight;
          if (currentDocY < selectionBottom - 0.5 &&
              !double.IsNaN(previousScrollY) &&
              Math.Abs(sliceState.ScrollY - previousScrollY) < 0.5 &&
              cssTop > 0.5)
          {
            break;
          }
          previousScrollY = sliceState.ScrollY;
        }

        if (IsNearlyUniformColor(stitched))
        {
          var domFallback = await CaptureDomMediaRegionAsync(core, selection);
          if (domFallback is not null && !IsNearlyUniformColor(domFallback))
          {
            stitched.Dispose();
            return domFallback;
          }
          domFallback?.Dispose();
        }

        return stitched;
      }
      catch
      {
        stitched.Dispose();
        throw;
      }
      finally
      {
        _ = ScrollAndSettleForCaptureAsync(core, initialState.ScrollX, initialState.ScrollY);
      }
    }
    catch
    {
      return null;
    }
  }

  private static Bitmap CropViewportBitmap(
      Bitmap source,
      double cssLeft,
      double cssTop,
      double cssWidth,
      double cssHeight,
      double scaleX,
      double scaleY)
  {
    var srcLeft = Math.Clamp((int)Math.Round(cssLeft * scaleX), 0, Math.Max(0, source.Width - 1));
    var srcTop = Math.Clamp((int)Math.Round(cssTop * scaleY), 0, Math.Max(0, source.Height - 1));
    var srcWidth = Math.Clamp((int)Math.Round(cssWidth * scaleX), 1, Math.Max(1, source.Width - srcLeft));
    var srcHeight = Math.Clamp((int)Math.Round(cssHeight * scaleY), 1, Math.Max(1, source.Height - srcTop));
    var cropped = new Bitmap(srcWidth, srcHeight, PixelFormat.Format32bppPArgb);
    using var graphics = Graphics.FromImage(cropped);
    graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
    graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
    graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
    graphics.DrawImage(
        source,
        new Rectangle(0, 0, srcWidth, srcHeight),
        new Rectangle(srcLeft, srcTop, srcWidth, srcHeight),
        GraphicsUnit.Pixel);
    return cropped;
  }

  private static async Task<CaptureViewportState?> GetCaptureViewportStateAsync(CoreWebView2? core)
  {
    if (core is null) return null;
    try
    {
      var json = await core.ExecuteScriptAsync("""
          (() => {
            document.getElementById('__omyaru_live_capture_host')?.remove();
            window.__omyaruHideFixedBars?.();
            const scroller = window.__omyaruActiveScroller;
            return {
              scrollX: scroller ? scroller.scrollLeft : window.scrollX,
              scrollY: scroller ? scroller.scrollTop : window.scrollY,
              viewportWidth: Math.max(1, window.innerWidth),
              viewportHeight: Math.max(1, window.innerHeight)
            };
          })()
          """);
      await Task.Delay(32);
      using var doc = JsonDocument.Parse(json);
      var root = doc.RootElement;
      if (root.ValueKind != JsonValueKind.Object) return null;
      return new CaptureViewportState(
          root.GetProperty("scrollX").GetDouble(),
          root.GetProperty("scrollY").GetDouble(),
          root.GetProperty("viewportWidth").GetDouble(),
          root.GetProperty("viewportHeight").GetDouble());
    }
    catch
    {
      return null;
    }
  }

  private static async Task<CaptureViewportState?> ScrollAndSettleForCaptureAsync(
      CoreWebView2? core,
      double targetScrollX,
      double targetScrollY)
  {
    if (core is null) return null;
    try
    {
      var xLiteral = targetScrollX.ToString(CultureInfo.InvariantCulture);
      var yLiteral = targetScrollY.ToString(CultureInfo.InvariantCulture);
      await core.ExecuteScriptAsync($$"""
          (() => {
            const scroller = window.__omyaruActiveScroller;
            if (scroller) {
              scroller.scrollLeft = {{xLiteral}};
              scroller.scrollTop = {{yLiteral}};
              scroller.dispatchEvent(new Event('scroll'));
            } else {
              window.scrollTo({{xLiteral}}, {{yLiteral}});
              window.dispatchEvent(new Event('scroll'));
            }
            for (const img of document.images) {
              try { if (img.loading === 'lazy') img.loading = 'eager'; } catch (_) {}
            }
            window.__omyaruHideFixedBars?.();
          })()
          """);
      CaptureViewportState? latestState = null;
      for (var attempt = 0; attempt < 8; attempt++)
      {
        await Task.Delay(32);
        var json = await core.ExecuteScriptAsync("""
            (() => {
              window.__omyaruHideFixedBars?.();
              const scroller = window.__omyaruActiveScroller;
              const visibleImages = [...document.images].filter(img => {
                const rect = img.getBoundingClientRect();
                return rect.bottom > 0 && rect.top < window.innerHeight && rect.width > 16 && rect.height > 16;
              });
              const ready = visibleImages.every(img => img.complete);
              return {
                ready,
                scrollX: scroller ? scroller.scrollLeft : window.scrollX,
                scrollY: scroller ? scroller.scrollTop : window.scrollY,
                viewportWidth: Math.max(1, window.innerWidth),
                viewportHeight: Math.max(1, window.innerHeight)
              };
            })()
            """);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) continue;
        latestState = new CaptureViewportState(
            root.GetProperty("scrollX").GetDouble(),
            root.GetProperty("scrollY").GetDouble(),
            root.GetProperty("viewportWidth").GetDouble(),
            root.GetProperty("viewportHeight").GetDouble());
        if (root.TryGetProperty("ready", out var readyProp) && readyProp.GetBoolean())
          break;
      }
      await Task.Delay(24);
      return latestState;
    }
    catch
    {
      return null;
    }
  }

  private static async Task<Bitmap?> CaptureViewportViaDevToolsAsync(CoreWebView2? core)
  {
    if (core is null) return null;
    try
    {
      var parameters = JsonSerializer.Serialize(new
      {
        format = "png",
        fromSurface = true,
        captureBeyondViewport = false,
      });
      var resultJson = await core
          .CallDevToolsProtocolMethodAsync("Page.captureScreenshot", parameters)
          .WaitAsync(TimeSpan.FromMilliseconds(850));
      using var result = JsonDocument.Parse(resultJson);
      var encoded = result.RootElement.GetProperty("data").GetString();
      if (string.IsNullOrWhiteSpace(encoded)) return null;
      using var stream = new MemoryStream(Convert.FromBase64String(encoded));
      using var source = Image.FromStream(stream);
      return new Bitmap(source);
    }
    catch
    {
      return null;
    }
  }

  private static async Task<Bitmap?> CaptureBestViewportFrameAsync(CoreWebView2? core)
  {
    var preview = await CaptureViewportBitmapAsync(core);
    if (preview is not null && !IsNearlyUniformColor(preview))
      return preview;
    var devTools = await CaptureViewportViaDevToolsAsync(core);
    if (devTools is not null && !IsNearlyUniformColor(devTools))
    {
      preview?.Dispose();
      return devTools;
    }
    if (preview is not null)
    {
      devTools?.Dispose();
      return preview;
    }
    return devTools;
  }

  internal static bool IsNearlyUniformColor(Bitmap bitmap)
  {
    if (bitmap.Width < 2 || bitmap.Height < 2) return true;
    var first = bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2);
    const int stepsX = 8;
    const int stepsY = 10;
    for (var iy = 0; iy < stepsY; iy++)
    {
      var y = Math.Clamp((int)((iy + 0.5) * bitmap.Height / stepsY), 0, bitmap.Height - 1);
      for (var ix = 0; ix < stepsX; ix++)
      {
        var x = Math.Clamp((int)((ix + 0.5) * bitmap.Width / stepsX), 0, bitmap.Width - 1);
        var c = bitmap.GetPixel(x, y);
        if (Math.Abs(c.R - first.R) > 3 ||
            Math.Abs(c.G - first.G) > 3 ||
            Math.Abs(c.B - first.B) > 3)
        {
          return false;
        }
      }
    }
    return true;
  }

  private static async Task<Bitmap?> CaptureDomMediaRegionAsync(CoreWebView2? core, LiveCaptureSelection selection)
  {
    if (core is null) return null;
    try
    {
      var selX = selection.X.ToString(CultureInfo.InvariantCulture);
      var selY = selection.Y.ToString(CultureInfo.InvariantCulture);
      var selW = selection.Width.ToString(CultureInfo.InvariantCulture);
      var selH = selection.Height.ToString(CultureInfo.InvariantCulture);
      var json = await core.ExecuteScriptAsync($$"""
          (() => {
            try {
              const scroller = window.__omyaruActiveScroller;
              const sx = scroller ? scroller.scrollLeft : window.scrollX;
              const sy = scroller ? scroller.scrollTop : window.scrollY;
              const selLeft = {{selX}};
              const selTop = {{selY}};
              const selWidth = {{selW}};
              const selHeight = {{selH}};
              const dpr = Math.min(2, Math.max(1, window.devicePixelRatio || 1));
              const outCanvas = document.createElement('canvas');
              outCanvas.width = Math.max(2, Math.round(selWidth * dpr));
              outCanvas.height = Math.max(2, Math.round(selHeight * dpr));
              const ctx = outCanvas.getContext('2d');
              if (!ctx) return '';
              ctx.scale(dpr, dpr);
              const bodyBg = getComputedStyle(document.body || document.documentElement).backgroundColor || '#1e1f21';
              ctx.fillStyle = bodyBg;
              ctx.fillRect(0, 0, selWidth, selHeight);
              let drawn = 0;
              for (const node of document.querySelectorAll('img, canvas')) {
                const style = getComputedStyle(node);
                if (style.display === 'none' || style.visibility === 'hidden' || Number.parseFloat(style.opacity) === 0) continue;
                const rect = node.getBoundingClientRect();
                if (rect.width < 8 || rect.height < 8) continue;
                const docLeft = rect.left + sx;
                const docTop = rect.top + sy;
                if (docLeft + rect.width <= selLeft || docLeft >= selLeft + selWidth ||
                    docTop + rect.height <= selTop || docTop >= selTop + selHeight) continue;
                try {
                  ctx.drawImage(node, docLeft - selLeft, docTop - selTop, rect.width, rect.height);
                  drawn++;
                } catch (_) {}
              }
              if (drawn === 0) return '';
              return outCanvas.toDataURL('image/png');
            } catch (_) {
              return '';
            }
          })()
          """);
      var dataUrl = JsonSerializer.Deserialize<string>(json);
      if (string.IsNullOrWhiteSpace(dataUrl)) return null;
      const string prefix = "data:image/png;base64,";
      if (!dataUrl.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
      var bytes = Convert.FromBase64String(dataUrl[prefix.Length..]);
      using var stream = new MemoryStream(bytes);
      using var source = Image.FromStream(stream);
      return new Bitmap(source);
    }
    catch
    {
      return null;
    }
  }

  private static async Task<string?> CaptureLiveLensFrameAsync(CoreWebView2? core)
  {
    if (core is null) return null;
    try
    {
      // The lens uses one already-composited viewport frame and moves it entirely
      // in JavaScript. Capturing on every pointer event caused both visible flashes
      // and a several-frame delay on image-heavy pages.
      using var previewStream = new MemoryStream();
      await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, previewStream);
      return "data:image/png;base64," + Convert.ToBase64String(previewStream.ToArray());
    }
    catch
    {
      return null;
    }
  }

  private static async Task InjectLiveCaptureOverlayAsync(
      CoreWebView2? core,
      string token,
      IReadOnlyList<string> folders,
      string activeFolder,
      bool showBookshelfPicker,
      string? lensFrame)
  {
    if (core is null) return;
    var tokenLiteral = JsonSerializer.Serialize(token);
    var foldersLiteral = JsonSerializer.Serialize(folders);
    var activeFolderLiteral = JsonSerializer.Serialize(activeFolder);
    var showBookshelfPickerLiteral = showBookshelfPicker ? "true" : "false";
    var lensFrameLiteral = JsonSerializer.Serialize(lensFrame ?? string.Empty);
    await core.ExecuteScriptAsync($$"""
        (() => {
          window.__omyaruRemoveCaptureOverlay?.();
          const token = {{tokenLiteral}};
          const shelfFolders = {{foldersLiteral}};
          const activeShelf = {{activeFolderLiteral}};
          const showBookshelfPicker = {{showBookshelfPickerLiteral}};
          const lensFrame = {{lensFrameLiteral}};
          const host = document.createElement('div');
          host.id = '__omyaru_live_capture_host';
          host.style.cssText = 'position:fixed;inset:0;z-index:2147483647;pointer-events:auto;';
          document.documentElement.appendChild(host);
          const shadow = host.attachShadow({ mode: 'open' });
          shadow.innerHTML = `
            <style>
              * { box-sizing: border-box; }
              #stage { position:fixed; inset:0; cursor:crosshair; user-select:none; touch-action:none; font-family:"Segoe UI",sans-serif; }
              #selection { display:none; position:fixed; border:1px solid #41ceff; border-radius:3px; pointer-events:none; z-index:4; }
              .mask { display:none; position:fixed; background:rgba(3,7,12,.48); pointer-events:none; z-index:2; }
              #guide-x,#guide-y { display:none; position:fixed; background:rgba(65,206,255,.9); pointer-events:none; z-index:5; }
              #guide-x { left:0; right:0; height:1px; }
              #guide-y { top:0; bottom:0; width:1px; }
              #lens { display:none; position:fixed; width:122px; height:122px; border:3px solid white; border-radius:50%; overflow:hidden; background:#191d22; box-shadow:0 8px 28px rgba(0,0,0,.45); pointer-events:none; z-index:8; contain:strict; transform:translateZ(0); backface-visibility:hidden; }
              #lens img { position:absolute; display:block; max-width:none; max-height:none; image-rendering:auto; transform:translateZ(0); will-change:left,top; }
              #lens::before,#lens::after { content:""; position:absolute; background:#41ceff; z-index:2; pointer-events:none; }
              #lens::before { left:0; right:0; top:50%; height:1px; }
              #lens::after { top:0; bottom:0; left:50%; width:1px; }
              #size { display:none; position:fixed; min-width:116px; height:34px; padding:0 16px; border-radius:17px; background:#328fff; color:white; font:600 14px/34px "Segoe UI",sans-serif; text-align:center; box-shadow:0 5px 18px rgba(0,0,0,.32); pointer-events:none; z-index:9; }
              #hint { position:fixed; top:18px; left:50%; transform:translateX(-50%); min-width:420px; padding:12px 18px; border:1px solid #4a525c; border-radius:20px; background:rgba(45,50,57,.97); color:#f4f0ee; text-align:center; box-shadow:0 12px 34px rgba(0,0,0,.4); pointer-events:none; z-index:10; }
              #hint strong { display:block; font:700 15px/20px "Segoe UI",sans-serif; }
              #hint span { color:#b7bcc5; font:400 12px/18px "Segoe UI",sans-serif; }
              #actions { position:fixed; right:22px; bottom:22px; display:flex; align-items:center; gap:8px; padding:7px; border:1px solid #4a525c; border-radius:23px; background:rgba(36,41,47,.98); box-shadow:0 12px 34px rgba(0,0,0,.45); z-index:11; }
              select { height:42px; min-width:170px; max-width:260px; padding:0 36px 0 15px; border:1px solid #4a525c; border-radius:19px; background:#30363d; color:#f4f0ee; font:600 14px "Segoe UI",sans-serif; outline:none; }
              button { height:42px; min-width:88px; padding:0 19px; border:1px solid #4a525c; border-radius:19px; background:#30363d; color:#f4f0ee; font:600 14px "Segoe UI",sans-serif; cursor:pointer; }
              button.primary { border-color:#5b92ff; background:#5b92ff; color:white; }
              button:disabled { opacity:.45; cursor:default; }
            </style>
            <div id="stage">
              <div id="mask-top" class="mask"></div><div id="mask-left" class="mask"></div>
              <div id="mask-right" class="mask"></div><div id="mask-bottom" class="mask"></div>
              <div id="selection"></div><div id="guide-x"></div><div id="guide-y"></div>
              <div id="lens"><img alt=""></div><div id="size"></div>
            </div>
            <div id="hint"><strong>?ㅼ떆媛??섏씠吏?먯꽌 罹≪쿂???곸뿭???쒕옒洹명븯?몄슂</strong><span>留덉슦???좊줈 ?대룞?섍굅??媛?μ옄由ш퉴吏 ?쒕옒洹명븯硫??섏씠吏媛 怨꾩냽 ?ㅽ겕濡ㅻ맗?덈떎.</span></div>
            <div id="actions"><select id="folder" aria-label="??ν븷 梨낆옣"></select><button id="cancel">痍⑥냼</button><button id="copy" disabled>蹂듭궗</button><button id="save" disabled>???/button><button id="complete" class="primary" disabled>梨낆옣?????/button></div>
          `;

          const stage = shadow.getElementById('stage');
          const selectionElement = shadow.getElementById('selection');
          const guideX = shadow.getElementById('guide-x');
          const guideY = shadow.getElementById('guide-y');
          const lens = shadow.getElementById('lens');
          const lensImage = lens.querySelector('img');
          const sizeBadge = shadow.getElementById('size');
          const complete = shadow.getElementById('complete');
          const copyButton = shadow.getElementById('copy');
          const saveButton = shadow.getElementById('save');
          const folderSelect = shadow.getElementById('folder');
          folderSelect.style.display = showBookshelfPicker ? '' : 'none';
          for (const folder of shelfFolders) {
            const option = document.createElement('option');
            option.value = folder;
            option.textContent = `?? ${folder}`;
            option.selected = folder === activeShelf;
            folderSelect.appendChild(option);
          }
          const masks = {
            top: shadow.getElementById('mask-top'), left: shadow.getElementById('mask-left'),
            right: shadow.getElementById('mask-right'), bottom: shadow.getElementById('mask-bottom')
          };
          let start = null;
          let end = null;
          let dragging = false;
          let lastClient = { x: 0, y: 0 };
          let animationFrame = 0;
          let lensRefreshTimer = 0;
          const scroller = window.__omyaruActiveScroller || null;
          const getScrollX = () => scroller ? scroller.scrollLeft : window.scrollX;
          const getScrollY = () => scroller ? scroller.scrollTop : window.scrollY;
          const scrollByDelta = (dx, dy) => {
            if (scroller) scroller.scrollBy({ left: dx, top: dy, behavior: 'auto' });
            else window.scrollBy({ left: dx, top: dy, behavior: 'auto' });
          };
          let lensOriginScrollX = getScrollX();
          let lensOriginScrollY = getScrollY();
          const lensZoom = 2.5;
          if (lensFrame) lensImage.src = lensFrame;

          const post = payload => chrome.webview.postMessage({ channel:'omyaru-live-capture', token, ...payload });
          const pagePoint = (clientX, clientY) => ({ x: clientX + getScrollX(), y: clientY + getScrollY() });
          const selected = () => {
            if (!start || !end) return null;
            const left = Math.min(start.x, end.x);
            const top = Math.min(start.y, end.y);
            return { left, top, width:Math.abs(end.x - start.x), height:Math.abs(end.y - start.y) };
          };
          const placeNearPointer = (element, width, height, x, y, gap) => {
            let left = x + gap;
            let top = y + gap;
            if (left + width > innerWidth - 12) left = x - width - gap;
            if (top + height > innerHeight - 12) top = y - height - gap;
            element.style.left = `${Math.max(12, Math.min(innerWidth - width - 12, left))}px`;
            element.style.top = `${Math.max(12, Math.min(innerHeight - height - 12, top))}px`;
          };
          const render = () => {
            const rect = selected();
            if (!rect || rect.width < 1 || rect.height < 1) {
              selectionElement.style.display = 'none';
              sizeBadge.style.display = 'none';
              complete.disabled = copyButton.disabled = saveButton.disabled = true;
              return;
            }
            const viewportLeft = rect.left - getScrollX();
            const viewportTop = rect.top - getScrollY();
            selectionElement.style.display = 'block';
            selectionElement.style.left = `${viewportLeft}px`;
            selectionElement.style.top = `${viewportTop}px`;
            selectionElement.style.width = `${rect.width}px`;
            selectionElement.style.height = `${rect.height}px`;
            complete.disabled = copyButton.disabled = saveButton.disabled = rect.width < 2 || rect.height < 2;
            sizeBadge.textContent = `?? ${Math.round(rect.width).toLocaleString()} 횞 ${Math.round(rect.height).toLocaleString()}`;
            sizeBadge.style.display = 'block';
            placeNearPointer(sizeBadge, Math.max(116, sizeBadge.offsetWidth), 34, lastClient.x, lastClient.y, 14);

            const visibleLeft = Math.max(0, viewportLeft);
            const visibleTop = Math.max(0, viewportTop);
            const visibleRight = Math.min(innerWidth, viewportLeft + rect.width);
            const visibleBottom = Math.min(innerHeight, viewportTop + rect.height);
            const showMask = !dragging && visibleRight > visibleLeft && visibleBottom > visibleTop;
            for (const mask of Object.values(masks)) mask.style.display = showMask ? 'block' : 'none';
            if (showMask) {
              Object.assign(masks.top.style, { left:'0px', top:'0px', width:'100vw', height:`${visibleTop}px` });
              Object.assign(masks.bottom.style, { left:'0px', top:`${visibleBottom}px`, width:'100vw', height:`${Math.max(0, innerHeight-visibleBottom)}px` });
              Object.assign(masks.left.style, { left:'0px', top:`${visibleTop}px`, width:`${visibleLeft}px`, height:`${visibleBottom-visibleTop}px` });
              Object.assign(masks.right.style, { left:`${visibleRight}px`, top:`${visibleTop}px`, width:`${Math.max(0, innerWidth-visibleRight)}px`, height:`${visibleBottom-visibleTop}px` });
            }
          };
          const updateLens = (clientX, clientY) => {
            const centerX = lens.clientWidth / 2;
            const centerY = lens.clientHeight / 2;
            const sourceX = clientX + getScrollX() - lensOriginScrollX;
            const sourceY = clientY + getScrollY() - lensOriginScrollY;
            lensImage.style.width = `${innerWidth * lensZoom}px`;
            lensImage.style.height = `${innerHeight * lensZoom}px`;
            lensImage.style.left = `${centerX - sourceX * lensZoom}px`;
            lensImage.style.top = `${centerY - sourceY * lensZoom}px`;
          };
          const scheduleLensRefresh = () => {
            clearTimeout(lensRefreshTimer);
            lensRefreshTimer = setTimeout(() => post({
              action:'refresh-lens',
              scrollX:getScrollX(),
              scrollY:getScrollY()
            }), 280);
          };
          const updatePointer = (clientX, clientY, updateSelection = true) => {
            lastClient = { x:clientX, y:clientY };
            const point = pagePoint(clientX, clientY);
            if (updateSelection) end = point;
            guideX.style.display = guideY.style.display = 'block';
            guideX.style.top = `${clientY}px`;
            guideY.style.left = `${clientX}px`;
            lens.style.display = 'block';
            placeNearPointer(lens, 122, 122, clientX, clientY, 28);
            updateLens(clientX, clientY);
            render();
          };
          const autoScroll = () => {
            if (!dragging) return;
            const edge = 64;
            let deltaX = 0;
            let deltaY = 0;
            if (lastClient.x < edge) deltaX = -Math.ceil((edge-lastClient.x)/edge*34)-6;
            else if (lastClient.x > innerWidth-edge) deltaX = Math.ceil((lastClient.x-(innerWidth-edge))/edge*34)+6;
            if (lastClient.y < edge) deltaY = -Math.ceil((edge-lastClient.y)/edge*38)-7;
            else if (lastClient.y > innerHeight-edge) deltaY = Math.ceil((lastClient.y-(innerHeight-edge))/edge*38)+7;
            if (deltaX || deltaY) {
              scrollByDelta(deltaX, deltaY);
              updatePointer(lastClient.x, lastClient.y);
            }
            animationFrame = requestAnimationFrame(autoScroll);
          };
          const pointerDown = event => {
            if (event.button === 2) {
              event.preventDefault();
              event.stopPropagation();
              cancel();
              return;
            }
            if (event.button !== 0) return;
            event.preventDefault();
            dragging = true;
            start = pagePoint(event.clientX, event.clientY);
            end = start;
            lastClient = { x:event.clientX, y:event.clientY };
            stage.setPointerCapture(event.pointerId);
            updatePointer(event.clientX, event.clientY);
            cancelAnimationFrame(animationFrame);
            animationFrame = requestAnimationFrame(autoScroll);
          };
          const pointerMove = event => {
            if (dragging) event.preventDefault();
            updatePointer(event.clientX, event.clientY, dragging);
          };
          const pointerUp = event => {
            if (!dragging) return;
            event.preventDefault();
            updatePointer(event.clientX, event.clientY);
            dragging = false;
            cancelAnimationFrame(animationFrame);
            render();
          };
          const pointerLeave = () => {
            if (dragging) return;
            guideX.style.display = guideY.style.display = lens.style.display = 'none';
          };
          const wheel = event => {
            event.preventDefault();
            scrollByDelta(event.deltaX, event.deltaY);
            render();
          };
          const submit = operation => {
            const rect = selected();
            if (!rect || rect.width < 2 || rect.height < 2) return;
            complete.disabled = copyButton.disabled = saveButton.disabled = true;
            host.style.display = 'none';
            requestAnimationFrame(() => requestAnimationFrame(() => post({
              action:'complete',
              operation,
              x:rect.left,
              y:rect.top,
              width:rect.width,
              height:rect.height,
              folder:folderSelect.value
            })));
          };
          const cancel = () => post({ action:'cancel' });
          const keyDown = event => {
            if (event.key === 'Escape') { event.preventDefault(); cancel(); }
            else if (event.key === 'Enter' && !complete.disabled) { event.preventDefault(); submit('bookshelf'); }
          };
          const contextMenu = event => {
            event.preventDefault();
            event.stopPropagation();
            cancel();
          };
          const scroll = () => {
            render();
            updateLens(lastClient.x, lastClient.y);
            scheduleLensRefresh();
          };
          stage.addEventListener('pointerdown', pointerDown);
          stage.addEventListener('pointermove', pointerMove);
          stage.addEventListener('pointerleave', pointerLeave);
          stage.addEventListener('pointerup', pointerUp);
          stage.addEventListener('pointercancel', pointerUp);
          stage.addEventListener('wheel', wheel, { passive:false });
          host.addEventListener('contextmenu', contextMenu, true);
          shadow.getElementById('cancel').addEventListener('click', cancel);
          copyButton.addEventListener('click', () => submit('copy'));
          saveButton.addEventListener('click', () => submit('save'));
          complete.addEventListener('click', () => submit('bookshelf'));
          window.addEventListener('keydown', keyDown, true);
          window.addEventListener('scroll', scroll, { passive:true });
          scroller?.addEventListener('scroll', scroll, { passive:true });

          window.__omyaruPrepareCaptureLensFrame = requestedToken => {
            if (requestedToken !== token) return;
            host.style.opacity = '0';
          };
          window.__omyaruRestoreCaptureLensFrame = requestedToken => {
            if (requestedToken !== token) return;
            host.style.opacity = '1';
          };
          window.__omyaruSetCaptureLensFrame = (requestedToken, dataUrl, originX, originY) => {
            if (requestedToken !== token || !dataUrl) return;
            lensOriginScrollX = originX;
            lensOriginScrollY = originY;
            lensImage.src = dataUrl;
            host.style.opacity = '1';
            updateLens(lastClient.x, lastClient.y);
          };
          updatePointer(innerWidth / 2, innerHeight / 2, false);

          window.__omyaruRemoveCaptureOverlay = requestedToken => {
            if (requestedToken && requestedToken !== token) return;
            cancelAnimationFrame(animationFrame);
            clearTimeout(lensRefreshTimer);
            window.removeEventListener('keydown', keyDown, true);
            window.removeEventListener('scroll', scroll);
            scroller?.removeEventListener('scroll', scroll);
            host.remove();
            delete window.__omyaruPrepareCaptureLensFrame;
            delete window.__omyaruRestoreCaptureLensFrame;
            delete window.__omyaruSetCaptureLensFrame;
            delete window.__omyaruRemoveCaptureOverlay;
          };
        })();
        """);
  }

  internal static async Task RemoveLiveCaptureOverlayAsync(CoreWebView2? core, string? token = null)
  {
    if (core is null) return;
    try
    {
      var tokenLiteral = token is null ? "undefined" : JsonSerializer.Serialize(token);
      await core.ExecuteScriptAsync($$"""
          window.__omyaruRemoveCaptureOverlay?.({{tokenLiteral}});
          """);
    }
    catch
    {
      // The page may already have navigated away from the injected selector.
    }
  }

  internal static async Task PreparePageForCaptureAsync(CoreWebView2? core)
  {
    if (core is null) return;
    await core.ExecuteScriptAsync("""
        (() => {
          try {
            if (window.__omyaruCaptureState) {
              const previous = window.__omyaruCaptureState;
              previous.items?.forEach(({ element, style }) => {
                if (!element) return;
                if (style === null) element.removeAttribute('style');
                else element.setAttribute('style', style);
              });
              previous.styleElement?.remove();
            }

            const detectPrimaryScroller = () => {
              const doc = document.scrollingElement || document.documentElement;
              const docRange = doc ? Math.max(0, doc.scrollHeight - window.innerHeight) : 0;
              let bestEl = null;
              let bestRange = docRange;
              for (const el of document.querySelectorAll('div, main, section, article')) {
                const style = getComputedStyle(el);
                if (style.overflowY !== 'auto' && style.overflowY !== 'scroll' && style.overflowY !== 'overlay') continue;
                const range = el.scrollHeight - el.clientHeight;
                if (range <= 8) continue;
                const rect = el.getBoundingClientRect();
                if (rect.width < window.innerWidth * 0.4 || rect.height < window.innerHeight * 0.4) continue;
                if (range > bestRange) {
                  bestRange = range;
                  bestEl = el;
                }
              }
              return bestEl;
            };
            const scroller = detectPrimaryScroller();
            window.__omyaruActiveScroller = scroller;

            for (const img of document.images) {
              try {
                if (img.loading === 'lazy') img.loading = 'eager';
              } catch (_) {}
            }

            const state = {
              items: [],
              styleElement: null,
              scroller,
              scrollX: scroller ? scroller.scrollLeft : window.scrollX,
              scrollY: scroller ? scroller.scrollTop : window.scrollY
            };
            window.__omyaruCaptureState = state;
            const remembered = new WeakSet();
            const hide = (element) => {
              if (!(element instanceof HTMLElement) || remembered.has(element)) return;
              remembered.add(element);
              state.items.push({ element, style: element.getAttribute('style') });
              // Keep the original layout stable while the selector is open. Collapsing
              // ad/sticky nodes forced a full reflow and made the page visibly flash.
              element.style.setProperty('visibility', 'hidden', 'important');
              element.style.setProperty('pointer-events', 'none', 'important');
            };

            const style = document.createElement('style');
            style.id = '__omyaru_capture_style';
            style.textContent = `
              ins.adsbygoogle,
              [data-ad-slot], [data-ad-client],
              iframe[src*="doubleclick"], iframe[src*="googlesyndication"],
              iframe[src*="adservice"], iframe[src*="adnxs"],
              [class~="advertisement"], [class*="advertisement"],
              [id*="advertisement"], [class^="ad-banner"], [id^="ad-banner"],
              [class^="ads-banner"], [id^="ads-banner"] {
                visibility: hidden !important;
                pointer-events: none !important;
              }
            `;
            (document.head || document.documentElement).appendChild(style);
            state.styleElement = style;

            const pageHost = location.hostname.toLowerCase();
            const isTokiReader = /^toki\d*\./i.test(pageHost) || /(^|\.)newtoki\./i.test(pageHost);
            const adWords = /(casino|bet(ting)?|banner|advert|ads?|sportsbook|jackpot|slot|?좏넗|移댁???諛고똿|踰좏똿|媛??泥レ땐|異⑹쟾)/i;
            const imageCandidates = [...document.images];
            const bannerImages = new WeakSet();
            const bannerOwners = [];
            for (const image of imageCandidates) {
              const anchor = image.closest('a');
              const owner = anchor || image;
              const rectangle = image.getBoundingClientRect();
              const width = image.naturalWidth || rectangle.width;
              const height = image.naturalHeight || rectangle.height;
              const ratio = height > 0 ? width / height : 0;
              const sourceText = `${image.currentSrc || image.src || ''} ${image.alt || ''} ${anchor?.href || ''}`;
              let externalLink = false;
              if (anchor?.href) {
                try { externalLink = new URL(anchor.href, location.href).hostname !== pageHost; } catch (_) {}
              }
              const compactWideBanner = ratio >= 2.15 && height > 0 && height <= 320;
              if (isTokiReader && compactWideBanner && (externalLink || adWords.test(sourceText))) {
                bannerImages.add(image);
                bannerOwners.push(owner);
              }
            }

            if (isTokiReader && bannerOwners.length > 1) {
              const wrapperCounts = new Map();
              for (const owner of bannerOwners) {
                let wrapper = owner.parentElement;
                for (let depth = 0; wrapper && depth < 4 && wrapper !== document.body; depth += 1, wrapper = wrapper.parentElement) {
                  wrapperCounts.set(wrapper, (wrapperCounts.get(wrapper) || 0) + 1);
                }
              }
              const wrappers = [...wrapperCounts.entries()]
                .sort((left, right) => right[0].contains(left[0]) ? -1 : left[0].contains(right[0]) ? 1 : 0);
              for (const [wrapper, count] of wrappers) {
                if (count < 2) continue;
                const images = [...wrapper.querySelectorAll('img')];
                const bannerCount = images.filter(image => bannerImages.has(image)).length;
                if (bannerCount >= 2 && bannerCount / Math.max(1, images.length) >= 0.65) hide(wrapper);
              }
            }
            for (const owner of bannerOwners) hide(owner);

            if (isTokiReader) {
              const adAnchors = [];
              for (const anchor of document.querySelectorAll('a[href]')) {
                const rectangle = anchor.getBoundingClientRect();
                const ratio = rectangle.height > 0 ? rectangle.width / rectangle.height : 0;
                let externalLink = false;
                try { externalLink = new URL(anchor.href, location.href).hostname !== pageHost; } catch (_) {}
                if (rectangle.width >= 180 && rectangle.height > 0 && rectangle.height <= 260 &&
                    ratio >= 2.15 && (externalLink || adWords.test(`${anchor.href} ${anchor.textContent || ''}`))) {
                  adAnchors.push(anchor);
                }
              }
              for (const anchor of adAnchors) hide(anchor);
            }

            const hideFixedBars = () => {
              const positionedCandidates = new Set([
                ...(document.body?.children || []),
                ...document.querySelectorAll('header, nav, footer, aside, [class*="header" i], [class*="footer" i], [class*="navbar" i], [class*="toolbar" i], [class*="floating" i], [style*="position: fixed" i], [style*="position:fixed" i], [style*="position: sticky" i], [style*="position:sticky" i]')
              ]);
              const maxBarHeight = Math.min(180, innerHeight * 0.24);
              const maxSideWidth = Math.min(240, innerWidth * 0.24);
              for (const element of positionedCandidates) {
                if (!(element instanceof HTMLElement) || element.id === '__omyaru_live_capture_host') continue;
                if (element === scroller || (scroller && element.contains(scroller))) continue;
                const computed = getComputedStyle(element);
                if (computed.position !== 'fixed' && computed.position !== 'sticky') continue;
                if (element.scrollHeight > element.clientHeight + 32) continue;
                const rectangle = element.getBoundingClientRect();
                if (rectangle.width <= 0 || rectangle.height <= 0) continue;
                const isThinBarOrWidget = rectangle.height <= maxBarHeight || rectangle.width <= maxSideWidth;
                if (!isThinBarOrWidget) continue;
                const containsLargeMedia = [...element.querySelectorAll('img, canvas')].some(media => {
                  const r = media.getBoundingClientRect();
                  return r.width >= 240 && r.height >= 200;
                });
                if (containsLargeMedia) continue;
                hide(element);
              }
            };
            window.__omyaruHideFixedBars = hideFixedBars;
            hideFixedBars();

            if (scroller) {
              scroller.scrollLeft = state.scrollX;
              scroller.scrollTop = state.scrollY;
            } else {
              window.scrollTo(state.scrollX, state.scrollY);
            }
            return { hidden: state.items.length, host: pageHost };
          } catch (_) {
            return { hidden: 0 };
          }
        })();
        """);
    await Task.Delay(24);
  }

  internal static async Task RestorePageAfterCaptureAsync(CoreWebView2? core)
  {
    if (core is null) return;
    try
    {
      await core.ExecuteScriptAsync("""
          (() => {
            const state = window.__omyaruCaptureState;
            if (!state) return;
            state.items?.forEach(({ element, style }) => {
              if (!element) return;
              if (style === null) element.removeAttribute('style');
              else element.setAttribute('style', style);
            });
            state.styleElement?.remove();
            if (state.scroller) {
              state.scroller.scrollLeft = state.scrollX || 0;
              state.scroller.scrollTop = state.scrollY || 0;
            } else {
              window.scrollTo(state.scrollX || 0, state.scrollY || 0);
            }
            delete window.__omyaruCaptureState;
            delete window.__omyaruActiveScroller;
            delete window.__omyaruHideFixedBars;
          })();
          """);
    }
    catch
    {
      // Navigation can complete while the selector is being prepared. In that case the old document is gone already.
    }
  }

  internal static async Task<Bitmap?> CaptureViewportBitmapAsync(CoreWebView2? core)
  {
    try
    {
      using var stream = new MemoryStream();
      await core!
          .CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream)
          .WaitAsync(TimeSpan.FromMilliseconds(850));
      stream.Position = 0;
      using var source = Image.FromStream(stream);
      return new Bitmap(source);
    }
    catch
    {
      return null;
    }
  }
}