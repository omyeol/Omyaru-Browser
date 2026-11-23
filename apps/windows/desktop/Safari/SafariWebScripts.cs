namespace PrivacyBrowser.Desktop.Safari;

internal static class SafariWebScripts
{
  /// <summary>
  /// Detects webpage theme color for macOS Safari Adaptive Toolbar Tinting and checks
  /// article readability for the "읽기 도구" address bar pill.
  /// </summary>
  internal const string PageMetadataProbe = """
      (() => {
        try {
          const meta = document.querySelector('meta[name="theme-color"]')?.getAttribute('content')?.trim() || '';
          const bodyBg = getComputedStyle(document.body || document.documentElement).backgroundColor || '';
          const htmlBg = getComputedStyle(document.documentElement).backgroundColor || '';
          const headerEl = document.querySelector('header, nav');
          const headerBg = headerEl ? getComputedStyle(headerEl).backgroundColor : '';
          const pick = (c) => c && c !== 'rgba(0, 0, 0, 0)' && c !== 'transparent' ? c : '';
          const color = meta || pick(headerBg) || pick(bodyBg) || pick(htmlBg) || '';

          const article = document.querySelector('article, main, [role="main"]');
          const paragraphs = [...(article || document.body || document.documentElement).querySelectorAll('p')]
            .filter(p => (p.innerText || '').trim().length >= 50);
          const textLen = paragraphs.reduce((acc, p) => acc + (p.innerText || '').trim().length, 0);
          const readerAvailable = paragraphs.length >= 3 && textLen >= 500 && !document.getElementById('__omyaru_reader_root');

          chrome.webview?.postMessage({
            channel: 'omyaru-page-meta',
            color,
            readerAvailable,
            readerActive: !!document.getElementById('__omyaru_reader_root'),
            hiddenCount: window.__omyaruHiddenElements?.length || 0
          });
        } catch (_) {}
      })();
      """;

  /// <summary>
  /// macOS 27 Safari Reader Mode overlay with 4 themes, font switcher, zoom, and auto TOC.
  /// </summary>
  internal static string ToggleReaderScript(string theme = "charcoal", string font = "Segoe UI", int fontSize = 18) => $$"""
      (() => {
        const existing = document.getElementById('__omyaru_reader_root');
        if (existing) {
          existing.style.opacity = '0';
          setTimeout(() => existing.remove(), 180);
          document.documentElement.style.overflow = existing.dataset.prevOverflow || '';
          chrome.webview?.postMessage({ channel: 'omyaru-reader-state', active: false });
          return false;
        }
        const themes = {
          white:    { bg: '#ffffff', fg: '#1d1d1f', muted: '#6e6e73', card: '#f5f5f7', border: 'rgba(0,0,0,0.08)' },
          sepia:    { bg: '#f8f1e3', fg: '#3b2f21', muted: '#7c6a56', card: '#efe5d2', border: 'rgba(59,47,33,0.12)' },
          charcoal: { bg: '#25262b', fg: '#eceef2', muted: '#a0a6b3', card: '#2e3038', border: 'rgba(255,255,255,0.10)' },
          black:    { bg: '#0c0d10', fg: '#e8eaef', muted: '#8f96a6', card: '#17191f', border: 'rgba(255,255,255,0.10)' }
        };
        const t = themes['{{theme}}'] || themes.charcoal;
        const source = document.querySelector('article') || document.querySelector('main') || document.querySelector('[role="main"]') || document.body;
        const title = (document.querySelector('h1')?.innerText || document.title || location.hostname).trim();
        const blocks = [];
        const toc = [];
        const nodes = source ? source.querySelectorAll('h1, h2, h3, p, blockquote, pre, ul, ol, img') : [];
        let headingIdx = 0;
        for (const el of nodes) {
          if (el.closest('nav, footer, aside, [aria-hidden="true"]')) continue;
          const tag = el.tagName.toLowerCase();
          if (tag === 'img') {
            const rect = el.getBoundingClientRect();
            if (rect.width >= 180 && rect.height >= 120 && el.src) {
              blocks.push(`<figure style="margin:24px 0;text-align:center"><img src="${el.src}" style="max-width:100%;border-radius:12px;box-shadow:0 8px 24px rgba(0,0,0,0.22)"/></figure>`);
            }
            continue;
          }
          const txt = (el.innerText || '').trim();
          if (txt.length < (tag === 'p' ? 20 : 2)) continue;
          const esc = txt.replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;');
          if (tag === 'h2' || tag === 'h3') {
            const hid = '__omyaru_toc_' + (++headingIdx);
            toc.push({ id: hid, text: txt.slice(0, 60) });
            blocks.push(`<${tag} id="${hid}" style="margin:28px 0 12px;line-height:1.35;font-weight:700">${esc}</${tag}>`);
          } else if (tag === 'blockquote') {
            blocks.push(`<blockquote style="margin:20px 0;padding:12px 18px;border-left:3px solid #0a84ff;background:${t.card};border-radius:0 10px 10px 0">${esc}</blockquote>`);
          } else if (tag === 'pre') {
            blocks.push(`<pre style="margin:18px 0;padding:14px 16px;background:${t.card};border-radius:10px;overflow:auto;font-size:0.88em">${esc}</pre>`);
          } else {
            blocks.push(`<p style="margin:0 0 18px;line-height:1.82">${esc}</p>`);
          }
        }
        if (blocks.length === 0) {
          const fallback = (document.body?.innerText || '').trim().slice(0, 12000).replace(/&/g,'&amp;').replace(/</g,'&lt;');
          blocks.push(`<p style="white-space:pre-wrap;line-height:1.8">${fallback}</p>`);
        }

        const root = document.createElement('div');
        root.id = '__omyaru_reader_root';
        root.dataset.prevOverflow = document.documentElement.style.overflow || '';
        document.documentElement.style.overflow = 'hidden';
        root.style.cssText = `position:fixed;inset:0;z-index:2147483645;background:${t.bg};color:${t.fg};overflow-y:auto;font-family:"{{font}}","Malgun Gothic",sans-serif;font-size:{{fontSize}}px;opacity:0;transition:opacity 200ms cubic-bezier(.22,1,.36,1);`;
        const tocHtml = toc.length >= 2
          ? `<aside style="position:sticky;top:36px;align-self:start;width:230px;flex-shrink:0;padding:16px;border-radius:14px;background:${t.card};border:1px solid ${t.border};font-size:13px"><div style="font-weight:700;margin-bottom:10px;color:${t.muted}">목차</div>${toc.map((h,i)=>`<a href="#${h.id}" style="display:block;color:${t.fg};text-decoration:none;padding:5px 0;opacity:0.88">${i+1}. ${h.text.replace(/</g,'&lt;')}</a>`).join('')}</aside>`
          : '';
        root.innerHTML = `
          <div style="max-width:980px;margin:0 auto;padding:48px 32px 96px;display:flex;gap:36px;align-items:flex-start">
            <article style="flex:1;min-width:0;max-width:680px;margin:0 auto">
              <div style="font-size:13px;color:${t.muted};margin-bottom:10px">${location.hostname} · Safari 읽기 도구</div>
              <h1 style="font-size:1.85em;line-height:1.25;margin:0 0 28px;font-weight:800;letter-spacing:-0.02em">${title.replace(/</g,'&lt;')}</h1>
              <div>${blocks.join('')}</div>
            </article>
            ${tocHtml}
          </div>`;
        document.documentElement.appendChild(root);
        requestAnimationFrame(() => root.style.opacity = '1');
        chrome.webview?.postMessage({ channel: 'omyaru-reader-state', active: true });
        return true;
      })();
      """;

  internal static string UpdateReaderStyleScript(string theme, string font, int fontSize) => $$"""
      (() => {
        const root = document.getElementById('__omyaru_reader_root');
        if (!root) return;
        const themes = {
          white:    { bg: '#ffffff', fg: '#1d1d1f' },
          sepia:    { bg: '#f8f1e3', fg: '#3b2f21' },
          charcoal: { bg: '#25262b', fg: '#eceef2' },
          black:    { bg: '#0c0d10', fg: '#e8eaef' }
        };
        const t = themes['{{theme}}'] || themes.charcoal;
        root.style.background = t.bg;
        root.style.color = t.fg;
        root.style.fontFamily = '"{{font}}","Malgun Gothic",sans-serif';
        root.style.fontSize = '{{fontSize}}px';
      })();
      """;

  /// <summary>
  /// macOS 27 Safari Distraction Control with GPU Canvas particle dissolve ("Thanos snap") animation!
  /// </summary>
  internal const string ToggleDistractionControlScript = """
      (() => {
        if (window.__omyaruDistractionActive) {
          window.__omyaruStopDistraction?.();
          return false;
        }
        window.__omyaruDistractionActive = true;
        window.__omyaruHiddenElements = window.__omyaruHiddenElements || [];

        const highlight = document.createElement('div');
        highlight.id = '__omyaru_dc_highlight';
        highlight.style.cssText = 'position:fixed;pointer-events:none;z-index:2147483646;border:2px solid #0a84ff;background:rgba(10,132,255,0.14);border-radius:10px;transition:all 90ms cubic-bezier(.22,1,.36,1);display:none;box-shadow:0 0 0 1px rgba(255,255,255,0.25) inset;';
        document.documentElement.appendChild(highlight);

        let hovered = null;
        const spawnParticles = (rect, bgColor) => {
          const canvas = document.createElement('canvas');
          const dpr = Math.min(window.devicePixelRatio || 1, 2);
          const pad = 48;
          canvas.width = Math.max(1, Math.floor((rect.width + pad * 2) * dpr));
          canvas.height = Math.max(1, Math.floor((rect.height + pad * 2) * dpr));
          canvas.style.cssText = `position:fixed;left:${rect.left - pad}px;top:${rect.top - pad}px;width:${rect.width + pad * 2}px;height:${rect.height + pad * 2}px;pointer-events:none;z-index:2147483647;`;
          document.documentElement.appendChild(canvas);
          const ctx = canvas.getContext('2d');
          ctx.scale(dpr, dpr);
          const count = Math.min(180, Math.max(45, Math.floor((rect.width * rect.height) / 900)));
          const particles = Array.from({ length: count }, () => ({
            x: pad + Math.random() * rect.width,
            y: pad + Math.random() * rect.height,
            vx: (Math.random() - 0.35) * 3.8,
            vy: -Math.random() * 2.6 - 0.5,
            size: 2.2 + Math.random() * 4.2,
            alpha: 0.75 + Math.random() * 0.25,
            decay: 0.024 + Math.random() * 0.025
          }));
          const fill = (bgColor && bgColor !== 'rgba(0, 0, 0, 0)' && bgColor !== 'transparent') ? bgColor : 'rgba(190,210,255,0.85)';
          const tick = () => {
            ctx.clearRect(0, 0, rect.width + pad * 2, rect.height + pad * 2);
            let alive = false;
            for (const p of particles) {
              if (p.alpha <= 0.01) continue;
              alive = true;
              p.x += p.vx;
              p.y += p.vy;
              p.vx *= 0.985;
              p.alpha -= p.decay;
              ctx.globalAlpha = Math.max(0, p.alpha);
              ctx.fillStyle = fill;
              ctx.beginPath();
              ctx.arc(p.x, p.y, p.size * 0.5, 0, Math.PI * 2);
              ctx.fill();
            }
            if (alive) requestAnimationFrame(tick);
            else canvas.remove();
          };
          requestAnimationFrame(tick);
        };

        const onMove = (e) => {
          const el = document.elementFromPoint(e.clientX, e.clientY);
          if (!el || el === document.body || el === document.documentElement || el.id?.startsWith('__omyaru_')) {
            highlight.style.display = 'none';
            hovered = null;
            return;
          }
          hovered = el;
          const r = el.getBoundingClientRect();
          highlight.style.display = 'block';
          highlight.style.left = r.left + 'px';
          highlight.style.top = r.top + 'px';
          highlight.style.width = r.width + 'px';
          highlight.style.height = r.height + 'px';
        };

        const onClick = (e) => {
          if (!hovered) return;
          e.preventDefault();
          e.stopPropagation();
          const target = hovered;
          const rect = target.getBoundingClientRect();
          const bg = getComputedStyle(target).backgroundColor;
          window.__omyaruHiddenElements.push({ el: target, prevDisplay: target.style.display });
          spawnParticles(rect, bg);
          target.style.transition = 'opacity 180ms ease';
          target.style.opacity = '0';
          setTimeout(() => { target.style.display = 'none'; target.style.opacity = ''; }, 170);
          highlight.style.display = 'none';
          hovered = null;
          chrome.webview?.postMessage({
            channel: 'omyaru-distraction-state',
            active: true,
            hiddenCount: window.__omyaruHiddenElements.length
          });
        };

        const onKey = (e) => { if (e.key === 'Escape') window.__omyaruStopDistraction?.(); };
        window.addEventListener('mousemove', onMove, true);
        window.addEventListener('click', onClick, true);
        window.addEventListener('keydown', onKey, true);
        window.__omyaruStopDistraction = () => {
          window.__omyaruDistractionActive = false;
          window.removeEventListener('mousemove', onMove, true);
          window.removeEventListener('click', onClick, true);
          window.removeEventListener('keydown', onKey, true);
          highlight.remove();
          chrome.webview?.postMessage({
            channel: 'omyaru-distraction-state',
            active: false,
            hiddenCount: window.__omyaruHiddenElements?.length || 0
          });
        };
        chrome.webview?.postMessage({
          channel: 'omyaru-distraction-state',
          active: true,
          hiddenCount: window.__omyaruHiddenElements.length
        });
        return true;
      })();
      """;

  internal const string RestoreDistractionHiddenScript = """
      (() => {
        const list = window.__omyaruHiddenElements || [];
        for (const item of list) {
          if (item.el) item.el.style.display = item.prevDisplay || '';
        }
        window.__omyaruHiddenElements = [];
        window.__omyaruStopDistraction?.();
        chrome.webview?.postMessage({ channel: 'omyaru-distraction-state', active: false, hiddenCount: 0 });
      })();
      """;

  internal const string ExtensionStoreBridgeScript = """
      (() => {
        if (window.__omyaruStoreBridgeActive) {
          window.__omyaruRefreshStoreBanner?.();
          return;
        }
        window.__omyaruStoreBridgeActive = true;
        const isDetailUrl = () => {
          const href = location.href;
          return /chromewebstore\.google\.com\/detail\//i.test(href) ||
                 /chrome\.google\.com\/webstore\/detail\//i.test(href) ||
                 /microsoftedge\.microsoft\.com\/addons\/detail\//i.test(href) ||
                 /addons\.mozilla\.org\/[^/]+\/firefox\/addon\/[^/?#]+/i.test(href) ||
                 /addons\.mozilla\.org\/firefox\/addon\/[^/?#]+/i.test(href);
        };
        const requestInstall = (url) => {
          const badge = document.getElementById('__omyaru_store_install_btn');
          if (badge) {
            badge.textContent = '⏳ Omyaru에 확장 프로그램 설치 중...';
            badge.style.background = '#1c64f2';
          }
          chrome.webview.postMessage({ channel: 'omyaru-extension-install', url: url || location.href });
        };
        window.__omyaruMarkExtensionInstalled = (name) => {
          const badge = document.getElementById('__omyaru_store_install_btn');
          if (badge) {
            badge.textContent = `✓ '${name}' Omyaru 설치 완료`;
            badge.style.background = '#10b981';
          }
        };
        window.__omyaruMarkExtensionFailed = () => {
          const badge = document.getElementById('__omyaru_store_install_btn');
          if (badge) {
            badge.textContent = '🧩 Omyaru에 이 확장 프로그램 다시 설치';
            badge.style.background = '#0a84ff';
          }
        };
        const patchStoreButtons = () => {
          for (const notice of document.querySelectorAll('.AMInstallButton-warning, .GetFirefoxButton, [class*="FirefoxBanner" i]')) {
            if (notice instanceof HTMLElement) notice.style.display = 'none';
          }
          for (const anchor of document.querySelectorAll('a[href$=".xpi"], a[href*=".xpi?"], a.AMInstallButton-button')) {
            if (!(anchor instanceof HTMLAnchorElement) || anchor.dataset.omyaruHooked === '1') continue;
            anchor.dataset.omyaruHooked = '1';
            anchor.textContent = '🧩 Omyaru에 설치';
            anchor.style.background = '#0a84ff';
            anchor.style.color = '#ffffff';
            anchor.style.borderRadius = '10px';
            anchor.addEventListener('click', (e) => { e.preventDefault(); e.stopPropagation(); requestInstall(anchor.href || location.href); }, true);
          }
          for (const btn of document.querySelectorAll('button')) {
            if (btn.dataset.omyaruHooked === '1') continue;
            const label = (btn.textContent || '').trim();
            if (/^(Chrome에 추가|Add to Chrome|Firefox에 추가|Add to Firefox|받기|Get)$/i.test(label)) {
              btn.dataset.omyaruHooked = '1';
              btn.disabled = false;
              btn.removeAttribute('disabled');
              btn.removeAttribute('aria-disabled');
              btn.addEventListener('click', (e) => { e.preventDefault(); e.stopPropagation(); requestInstall(location.href); }, true);
            }
          }
        };
        const ensureBanner = () => {
          let host = document.getElementById('__omyaru_store_banner');
          if (!isDetailUrl()) { if (host) host.style.display = 'none'; return; }
          if (!host) {
            host = document.createElement('div');
            host.id = '__omyaru_store_banner';
            host.style.cssText = 'position:fixed;right:22px;bottom:22px;z-index:2147483646;display:flex;align-items:center;gap:10px;padding:10px 14px;background:rgba(28,28,30,0.94);backdrop-filter:blur(16px);border:1px solid rgba(255,255,255,0.16);border-radius:16px;box-shadow:0 12px 32px rgba(0,0,0,0.45);font-family:"Segoe UI",system-ui,sans-serif;';
            const btn = document.createElement('button');
            btn.id = '__omyaru_store_install_btn';
            btn.type = 'button';
            btn.textContent = '🧩 Omyaru에 이 확장 프로그램 설치';
            btn.style.cssText = 'border:none;outline:none;cursor:pointer;padding:10px 18px;border-radius:12px;background:#0a84ff;color:#fff;font-size:14px;font-weight:600;';
            btn.addEventListener('click', () => requestInstall(location.href));
            host.appendChild(btn);
            document.body.appendChild(host);
          } else host.style.display = 'flex';
          patchStoreButtons();
        };
        window.__omyaruRefreshStoreBanner = ensureBanner;
        ensureBanner();
        new MutationObserver(() => ensureBanner()).observe(document.documentElement, { childList: true, subtree: true });
      })();
      """;
}
