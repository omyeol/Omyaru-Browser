# Omyaru iOS (v0.15.18 — Full Android v0.15.18 Feature Parity)

SwiftUI / WebKit (`WKWebView`) browser and bookshelf app for iPhone and iPad aligned with Android Omyaru Browser `v0.15.18` (`versionCode 76`):

- **Profiles, Tab Groups & Private Mode**: Multiple profiles (`개인`, custom profiles with 18 icons, palette/custom HEX color picker), tab groups (`기본`, custom groups), and non-persistent private tabs (`비공개`).
- **Chrome, Address Bar & Page Tools**:
  - Top/Bottom address bar placement, Liquid Glass UI, scroll-based toolbar collapse & pull-up restore pill
  - 20 customizable toolbar shortcuts (`ToolbarEditorSheet`)
  - `PageToolsSheet` (`[- 5%] [100% picker] [+ 5%]` + 4-button grid: `확장프로그램`, `페이지 찾기`, `차단 끄기/켜기`, `데스크톱/모바일`)
- **Tab Management (`v0.15.18` Parity)**:
  - Lazy background tab restoration on startup
  - Grid (`1~10` columns) & List overview, selectable card aspect ratios (`1:1`, `3:4`, `16:9`), bottom-first ordering (`탭을 아래에서부터 시작`)
  - Tab duplication (`탭 복제`) inserted immediately after the source tab (`index + 1`)
  - Long-press drag reorder with top/bottom edge auto-scroll
  - Animated tab close (`spring`) & Undo Close Tab (`닫은 탭 복원`)
- **Bookmarks, History & Downloads**:
  - Hierarchical bookmark folders & item editor (`BookmarksSheet`)
  - Date-grouped browsing history (`오늘`, `어제`, `이번 주`, `이전 기록`) (`HistorySheet`)
  - Download confirmation prompt (`DownloadConfirmationSheet`) with filename editing and direct save-to-Bookshelf + inline folder creation
- **Web Engine & Extensions**:
  - Search engine presets (`Google`, `DuckDuckGo`, `Brave`, `Startpage`, `Naver`, `Bing`, Custom template)
  - Tracking protection (`Off` / `Standard` / `Strict`), Cookie policy, Global Privacy Control (`GPC`), per-site shield exceptions
  - Built-in Web Image Preloader (`IntersectionObserver` rootMargin expansion + `data-src`/`data-original` promotion within `2200px` below viewport)
  - Native WebKit Content Blocker (`WKContentRuleListStore`) + cosmetic ad/cookie hider + copy/context-menu unlocker + force web dark mode (`PageColorSchemeChoice`)
  - Custom UserScript (`.user.js`) installer & code editor
- **Live Webpage Region Capture (`LiveCaptureView.swift`)**:
  - Fixed header/ad hiding, interactive 4-corner selection handles with magnifier lens, side-margin capture toggle, incremental **`↓ 아래로 확장`** scroll-and-stitch capture, watermark handoff, and direct save to Bookshelf, Photos/Files PNG, or Clipboard.
- **Dual-Mode Watermark Remover (`WatermarkRemoverView.swift`)**:
  - **Built-in Offline Inpainting**: multi-box selection, automatic corner detection, bidirectional edge-interpolation inpainting, before/after toggle
  - **Web Tool Auto-Inject (`웹 도구 · ⚡ 사진 주입`)**: loads configurable `watermarkRemoverUrl` in an embedded `WKWebView`, injects the captured PNG into `<input type="file">` via `DataTransfer`, and intercepts result downloads straight into Bookshelf.
- **Hierarchical Folder Tree Bookshelf (`BookshelfView.swift`)**:
  - Hierarchical path tree (`상위폴더/하위폴더`) with expand/collapse, child folder creation, rename, order move, and folder/photo move dialogs with explicit **`[확인 (여기로 이동)]`** confirm buttons
  - Parent folder multi-column (`1~6열`), folder notes, multi-select batch move/delete, quick top/bottom jump buttons
  - Storage manager (`BookshelfStorageSheet`: disk usage MB, local vs cloud-only counts, download all cloud originals, orphan cleanup), fullscreen zoom viewer, multi-image import, clipboard paste, and PDF export (`세로 9:16` / `가로 16:9`).
- **Google Drive & Encrypted Sync (`DriveSyncEngine.swift`)**:
  - Cross-platform `privacy-browser-sync` (`PBKDF2-HMAC-SHA256` 210,000 iterations + `AES-256-GCM`) with `OmyaruDriveKeyRing` & legacy envelope migration, wire-compatible with Android and Windows
  - Syncs `omyaru-userdata-v1-` (bookmark folders, bookmarks, history), `omyaru-tabs-v2-` (profiles & tabs), `omyaru-shelf-v1-` + 1MB chunked image blobs (`omyaru-shelf-chunk-v1-*`) with SHA-256 verification and on-demand cloud download, `DriveTabReset` (`"동기화 탭 초기화"`), and `.omyarusync` encrypted bundle export/import.
- **Security & Network (`AppLockAndPrivacy.swift`)**:
  - Face ID / Touch ID + PIN (`4~6자리`) & Password (`비밀번호`) App Lock with app-switcher black shield (`appSwitchBlackShield`)
  - DoH Encrypted DNS presets (`1.1.1.1`, `8.8.8.8`, `9.9.9.9`, `AdGuard`, custom DoH) + `.mobileconfig` generator, WireGuard `.conf` profile validator/manager, connectivity & external IP check, and SOCKS5/HTTP proxy config.

AltStore Classic source URL:
`https://raw.githubusercontent.com/omyeol/Omyaru-Browser/main/apps/ios/altstore-source.json`
