# Omyaru iOS (v0.2.0 — Android Feature Parity)

SwiftUI / WebKit (`WKWebView`) browser and bookshelf app for iPhone and iPad with Android Omyaru Browser feature parity:

- **Profiles, Tab Groups & Private Mode**: Multiple profiles (`개인`, custom profiles), tab groups (`기본`, custom groups), and non-persistent private tabs (`비공개`).
- **Tab Management (`v0.15.17`/`v0.15.18` Parity)**:
  - Lazy background tab restoration on startup
  - Tab duplication (`탭 복제`) inserted immediately after the source tab (`index + 1`)
  - Long-press drag reorder with top/bottom edge auto-scroll
  - Animated tab close (`spring`) & Undo Close Tab (`닫은 탭 복원`)
  - Grid/List overview with live page thumbnails & title/URL search
- **Web Engine & Extensions**:
  - Built-in Web Image Preloader (`IntersectionObserver` rootMargin expansion + `data-src`/`data-original` promotion within `2200px` below viewport)
  - Native WebKit Content Blocker (`WKContentRuleListStore`) + cosmetic ad/cookie hider + copy/context-menu unlocker + force web dark mode
  - Custom UserScript (`.user.js`) installer & editor
  - Find in Page, Desktop/Mobile User-Agent toggle, Page Zoom (`50%~200%`), Customizable bottom toolbar shortcuts
- **Live Webpage Region Capture (`LiveCaptureView.swift`)**:
  - Fixed header/ad hiding, interactive selection handles with magnifier lens, multi-viewport (`1~6` screens) scroll-and-stitch capture, and direct save to Bookshelf, Photos/Files PNG, or Clipboard.
- **Built-in Watermark Remover (`WatermarkRemoverView.swift`)**:
  - Webpage capture or photo/file import, multi-box watermark selection, automatic corner detection, bidirectional edge-interpolation inpainting, before/after comparison, and direct Bookshelf integration.
- **Hierarchical Folder Tree Bookshelf (`BookshelfView.swift`)**:
  - Hierarchical path tree (`상위폴더/하위폴더`) with expand/collapse, child folder creation, rename, order move, and folder/photo move dialog with explicit **`[확인 (여기로 이동)]`** confirm buttons
  - Parent folder multi-column (`1~6열`) and folder note preservation
  - Fullscreen & zoomable viewer, multi-image import, clipboard paste, and PDF export (`세로 9:16` / `가로 16:9`).
- **Google Drive & Encrypted Sync (`DriveSyncEngine.swift`)**:
  - Cross-platform `privacy-browser-sync` (`PBKDF2-HMAC-SHA256` 210,000 iterations + `AES-256-GCM`) wire-compatible with Android and Windows (`appDataFolder` + `.omyarusync` encrypted bundle export/import).
- **Security & Network (`AppLockAndPrivacy.swift`)**:
  - Face ID / Touch ID + PBKDF2 PIN App Lock with background blur shield
  - DoH Encrypted DNS presets (`1.1.1.1`, `8.8.8.8`, `9.9.9.9`, `AdGuard`) + `.mobileconfig` generator & proxy settings.

AltStore Classic source URL:
`https://raw.githubusercontent.com/omyeol/Omyaru-Browser/main/apps/ios/altstore-source.json`
