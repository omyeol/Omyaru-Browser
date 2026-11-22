import SwiftUI
import WebKit
import UniformTypeIdentifiers
import UIKit

@main
struct OmyaruApp: App {
    @Environment(\.scenePhase) private var scenePhase
    @StateObject private var browser = BrowserModel()
    @ObservedObject private var appLock = AppLockManager.shared
    @ObservedObject private var driveSync = DriveSyncManager.shared

    var body: some Scene {
        WindowGroup {
            ZStack {
                BrowserScreen()
                    .environmentObject(browser)

                if appLock.isLocked {
                    AppLockOverlayView()
                        .transition(.opacity)
                }

                if scenePhase != .active && appLock.isEnabled {
                    Color(red: 0.05, green: 0.05, blue: 0.06)
                        .ignoresSafeArea()
                        .overlay {
                            Image(systemName: "lock.shield.fill")
                                .font(.system(size: 56))
                                .foregroundStyle(.blue)
                        }
                }
            }
            .onChange(of: scenePhase) { phase in
                switch phase {
                case .background:
                    appLock.lockIfNeeded()
                    browser.persist()
                case .active:
                    if driveSync.autoSyncEnabled && driveSync.isConnected {
                        Task {
                            await driveSync.syncWithGoogleDrive(browser: browser, shelf: .shared)
                        }
                    }
                default:
                    break
                }
            }
            .onOpenURL { url in
                browser.open(url.absoluteString)
            }
        }
    }
}

// MARK: - Core Browser Models (Android & Windows Parity)

struct BrowserProfile: Codable, Identifiable, Hashable {
    var id: UUID = UUID()
    var name: String
    var icon: String = "person.fill"
    var colorHex: String = "#0A84FF"
}

struct BrowserGroup: Codable, Identifiable, Hashable {
    var id: UUID = UUID()
    var profileID: UUID
    var name: String
}

struct BrowserTab: Codable, Identifiable, Hashable {
    var id: UUID = UUID()
    var title: String = "새 탭"
    var url: String = "about:blank"
    var profileID: UUID
    var groupID: UUID
    var isPrivate: Bool = false
    var isDesktopMode: Bool = false
    var pageZoom: Double = 1.0
}

struct BrowserBookmark: Codable, Identifiable, Hashable {
    var id: UUID = UUID()
    var title: String
    var url: String
    var folder: String = ""
}

struct HistoryEntry: Codable, Identifiable, Hashable {
    var id: UUID = UUID()
    var title: String
    var url: String
    var visitedAtEpochMs: Int64 = Int64(Date().timeIntervalSince1970 * 1000)
}

enum SearchEngineChoice: String, CaseIterable, Codable, Identifiable {
    case duckduckgo = "DuckDuckGo"
    case google = "Google"
    case naver = "Naver"
    case bing = "Bing"

    var id: String { rawValue }

    func searchURL(for query: String) -> URL? {
        let encoded = query.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed) ?? query
        switch self {
        case .duckduckgo: return URL(string: "https://duckduckgo.com/?q=\(encoded)")
        case .google: return URL(string: "https://www.google.com/search?q=\(encoded)")
        case .naver: return URL(string: "https://search.naver.com/search.naver?query=\(encoded)")
        case .bing: return URL(string: "https://www.bing.com/search?q=\(encoded)")
        }
    }
}

enum ToolbarShortcutAction: String, CaseIterable, Codable, Identifiable {
    case newTab = "new_tab"
    case tabs = "tabs"
    case capture = "capture"
    case watermark = "watermark"
    case bookshelf = "bookshelf"
    case bookmarks = "bookmarks"
    case downloads = "downloads"
    case sync = "sync"
    case share = "share"
    case settings = "settings"

    var id: String { rawValue }

    var title: String {
        switch self {
        case .newTab: return "새 탭"
        case .tabs: return "탭 목록"
        case .capture: return "화면 캡처"
        case .watermark: return "워터마크 제거"
        case .bookshelf: return "책장"
        case .bookmarks: return "북마크"
        case .downloads: return "다운로드"
        case .sync: return "동기화"
        case .share: return "공유"
        case .settings: return "설정"
        }
    }

    var systemImage: String {
        switch self {
        case .newTab: return "plus"
        case .tabs: return "square.on.square"
        case .capture: return "viewfinder"
        case .watermark: return "wand.and.stars"
        case .bookshelf: return "books.vertical"
        case .bookmarks: return "book"
        case .downloads: return "arrow.down.circle"
        case .sync: return "arrow.triangle.2.circlepath.icloud"
        case .share: return "square.and.arrow.up"
        case .settings: return "gearshape"
        }
    }
}

struct BrowserPreferences: Codable, Equatable {
    var searchEngine: SearchEngineChoice = .duckduckgo
    var homePage: String = "about:blank"
    var automaticHTTPS: Bool = true
    var showFullURL: Bool = false
    var openLinksInNewTab: Bool = true
    var showTabThumbnails: Bool = true
    var compactToolbar: Bool = false
    var confirmBeforeDownload: Bool = false
    var blockTrackers: Bool = true
    var blockPopups: Bool = true
    var enableImagePreloader: Bool = true
    var hideCookieNotices: Bool = true
    var unlockCopyAndContextMenu: Bool = true
    var enableMediaPiPHelper: Bool = true
    var forceWebDarkMode: Bool = false
    var dnsPreset: DnsProviderPreset = .cloudflare
    var customDoHURL: String = "https://cloudflare-dns.com/dns-query"
    var proxyEnabled: Bool = false
    var proxyIsSOCKS5: Bool = true
    var proxyHost: String = "127.0.0.1"
    var proxyPort: Int = 1080
    var toolbarShortcuts: [ToolbarShortcutAction] = [.capture, .bookshelf, .tabs]
}

// MARK: - Main Browser ViewModel & WebKit Controller

@MainActor
final class BrowserModel: NSObject, ObservableObject, WKNavigationDelegate, WKUIDelegate, WKDownloadDelegate {
    static let privateGroupID = UUID(uuidString: "00000000-0000-0000-0000-000000000001")!
    static let defaultProfileID = UUID(uuidString: "00000000-0000-0000-0000-000000000010")!
    static let defaultGroupID = UUID(uuidString: "00000000-0000-0000-0000-000000000002")!

    @Published var profiles: [BrowserProfile]
    @Published var groups: [BrowserGroup]
    @Published var tabs: [BrowserTab]
    @Published var closedTabs: [BrowserTab] = []
    @Published var activeProfileID: UUID
    @Published var activeGroupID: UUID
    @Published var activeID: UUID
    @Published var bookmarkFolders: [String]
    @Published var bookmarks: [BrowserBookmark]
    @Published var history: [HistoryEntry]
    @Published var userScripts: [UserScriptExtension]
    @Published var preferences: BrowserPreferences
    @Published var downloads: [URL] = []
    @Published var tabSnapshots: [UUID: UIImage] = [:]
    @Published var blockedTrackerCount: Int = 0
    @Published var estimatedProgress: Double = 0.0
    @Published var isLoading: Bool = false
    @Published var findQuery: String = ""
    @Published var findMatchCount: Int = 0
    @Published var findCurrentIndex: Int = 0
    @Published var status: String = ""

    private var webViews: [UUID: WKWebView] = [:]
    private var loadedTabIDs: Set<UUID> = []
    private var compiledContentRuleList: WKContentRuleList? = nil

    private static let stateKey = "omyaru.ios.browser.v2"
    private static let prefsKey = "omyaru.ios.preferences.v2"

    private struct PersistedState: Codable {
        var profiles: [BrowserProfile]
        var groups: [BrowserGroup]
        var tabs: [BrowserTab]
        var activeProfileID: UUID
        var activeGroupID: UUID
        var activeID: UUID
        var bookmarkFolders: [String]
        var bookmarks: [BrowserBookmark]
        var history: [HistoryEntry]
        var userScripts: [UserScriptExtension]
        var blockedTrackerCount: Int
    }

    override init() {
        let defaultProfile = BrowserProfile(id: Self.defaultProfileID, name: "개인", icon: "person.fill", colorHex: "#0A84FF")
        let defaultGroup = BrowserGroup(id: Self.defaultGroupID, profileID: Self.defaultProfileID, name: "기본")
        let defaultBookmarks: [BrowserBookmark] = [
            BrowserBookmark(title: "DuckDuckGo", url: "https://duckduckgo.com"),
            BrowserBookmark(title: "네이버", url: "https://www.naver.com"),
            BrowserBookmark(title: "Google", url: "https://www.google.com"),
            BrowserBookmark(title: "YouTube", url: "https://m.youtube.com"),
            BrowserBookmark(title: "GitHub", url: "https://github.com"),
            BrowserBookmark(title: "나무위키", url: "https://namu.wiki")
        ]

        let savedPrefs = UserDefaults.standard.data(forKey: Self.prefsKey)
            .flatMap { try? JSONDecoder().decode(BrowserPreferences.self, from: $0) } ?? BrowserPreferences()
        preferences = savedPrefs

        if let data = UserDefaults.standard.data(forKey: Self.stateKey),
           let saved = try? JSONDecoder().decode(PersistedState.self, from: data) {
            let resolvedProfiles = saved.profiles.isEmpty ? [defaultProfile] : saved.profiles
            let resolvedGroups = saved.groups.isEmpty ? [defaultGroup] : saved.groups
            let regularTabs = saved.tabs.filter { !$0.isPrivate }
            let initialTab = BrowserTab(profileID: resolvedProfiles[0].id, groupID: resolvedGroups[0].id)
            let resolvedTabs = regularTabs.isEmpty ? [initialTab] : regularTabs
            profiles = resolvedProfiles
            groups = resolvedGroups
            tabs = resolvedTabs
            activeProfileID = resolvedProfiles.contains(where: { $0.id == saved.activeProfileID }) ? saved.activeProfileID : resolvedProfiles[0].id
            activeGroupID = saved.activeGroupID == Self.privateGroupID ? resolvedGroups[0].id : saved.activeGroupID
            activeID = resolvedTabs.contains(where: { $0.id == saved.activeID }) ? saved.activeID : resolvedTabs[0].id
            bookmarkFolders = saved.bookmarkFolders
            bookmarks = saved.bookmarks.isEmpty ? defaultBookmarks : saved.bookmarks
            history = saved.history
            userScripts = saved.userScripts
            blockedTrackerCount = saved.blockedTrackerCount
        } else {
            let initialTab = BrowserTab(profileID: Self.defaultProfileID, groupID: Self.defaultGroupID)
            profiles = [defaultProfile]
            groups = [defaultGroup]
            tabs = [initialTab]
            activeProfileID = Self.defaultProfileID
            activeGroupID = Self.defaultGroupID
            activeID = initialTab.id
            bookmarkFolders = ["즐겨찾기", "읽을거리"]
            bookmarks = defaultBookmarks
            history = []
            userScripts = []
            blockedTrackerCount = 0
        }
        super.init()
        refreshDownloadsList()
        loadPersistedSnapshotsAsync()
        compileTrackerBlockerRules()
    }

    // MARK: - Computed Properties

    var activeTab: BrowserTab? { tabs.first { $0.id == activeID } }
    var activeProfile: BrowserProfile { profiles.first { $0.id == activeProfileID } ?? profiles[0] }
    var currentURL: String { activeTab?.url ?? "about:blank" }
    var isStartPage: Bool { currentURL == "about:blank" || currentURL.isEmpty }
    var canGoBack: Bool { webViews[activeID]?.canGoBack ?? false }
    var canGoForward: Bool { webViews[activeID]?.canGoForward ?? false }
    var isCurrentBookmarked: Bool {
        guard !isStartPage else { return false }
        return bookmarks.contains { $0.url.caseInsensitiveCompare(currentURL) == .orderedSame }
    }

    var profileGroups: [BrowserGroup] {
        groups.filter { $0.profileID == activeProfileID }
    }

    var visibleTabs: [BrowserTab] {
        if activeGroupID == Self.privateGroupID {
            return tabs.filter { $0.isPrivate }
        }
        return tabs.filter { !$0.isPrivate && $0.profileID == activeProfileID && $0.groupID == activeGroupID }
    }

    func activeWebView() -> WKWebView? {
        guard let tab = activeTab else { return nil }
        return webView(for: tab.id)
    }

    // MARK: - Persistence

    func persist() {
        let regularTabs = tabs.filter { !$0.isPrivate }
        let state = PersistedState(
            profiles: profiles,
            groups: groups,
            tabs: regularTabs,
            activeProfileID: activeProfileID,
            activeGroupID: activeGroupID,
            activeID: activeID,
            bookmarkFolders: bookmarkFolders,
            bookmarks: bookmarks,
            history: Array(history.prefix(600)),
            userScripts: userScripts,
            blockedTrackerCount: blockedTrackerCount
        )
        if let data = try? JSONEncoder().encode(state) {
            UserDefaults.standard.set(data, forKey: Self.stateKey)
        }
    }

    func savePreferences() {
        if let data = try? JSONEncoder().encode(preferences) {
            UserDefaults.standard.set(data, forKey: Self.prefsKey)
        }
    }

    private var snapshotsDirectory: URL {
        let dir = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("TabSnapshots", isDirectory: true)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        return dir
    }

    private func loadPersistedSnapshotsAsync() {
        let ids = tabs.map(\.id)
        let dir = snapshotsDirectory
        Task.detached(priority: .utility) {
            var loaded: [UUID: UIImage] = [:]
            for id in ids {
                let path = dir.appendingPathComponent("\(id.uuidString).jpg").path
                if let img = UIImage(contentsOfFile: path) {
                    loaded[id] = img
                }
            }
            await MainActor.run { [loaded] in
                for (k, v) in loaded where self.tabSnapshots[k] == nil {
                    self.tabSnapshots[k] = v
                }
            }
        }
    }

    func captureSnapshot(for id: UUID) {
        guard preferences.showTabThumbnails, let view = webViews[id],
              let tab = tabs.first(where: { $0.id == id }), tab.url != "about:blank" else { return }
        let config = WKSnapshotConfiguration()
        config.snapshotWidth = 280
        view.takeSnapshot(with: config) { [weak self] image, _ in
            guard let self, let image else { return }
            self.tabSnapshots[id] = image
            if !tab.isPrivate, let jpg = image.jpegData(compressionQuality: 0.72) {
                let target = self.snapshotsDirectory.appendingPathComponent("\(id.uuidString).jpg")
                Task.detached(priority: .background) {
                    try? jpg.write(to: target, options: .atomic)
                }
            }
        }
    }

    // MARK: - WebKit Configuration, Image Preloader (v0.15.18 Parity), Tracker Blocker

    func webView(for id: UUID, loadIfNeeded: Bool = true) -> WKWebView {
        if let existing = webViews[id] {
            if loadIfNeeded && !loadedTabIDs.contains(id) {
                loadedTabIDs.insert(id)
                if let tab = tabs.first(where: { $0.id == id }), tab.url != "about:blank", let url = URL(string: tab.url) {
                    existing.load(URLRequest(url: url))
                }
            }
            return existing
        }

        let tab = tabs.first(where: { $0.id == id })
        let config = WKWebViewConfiguration()
        if tab?.isPrivate == true {
            config.websiteDataStore = .nonPersistent()
        } else {
            config.websiteDataStore = .default()
        }
        config.allowsInlineMediaPlayback = true
        config.allowsPictureInPictureMediaPlayback = true
        config.defaultWebpagePreferences.allowsContentJavaScript = true
        config.preferences.javaScriptCanOpenWindowsAutomatically = !preferences.blockPopups

        let controller = WKUserContentController()
        if preferences.blockTrackers, let ruleList = compiledContentRuleList {
            controller.add(ruleList)
        }
        installUserScripts(into: controller)
        config.userContentController = controller

        let view = WKWebView(frame: .zero, configuration: config)
        view.allowsBackForwardNavigationGestures = true
        view.navigationDelegate = self
        view.uiDelegate = self
        view.isOpaque = false
        view.backgroundColor = UIColor(red: 0.07, green: 0.07, blue: 0.08, alpha: 1.0)
        if tab?.isDesktopMode == true {
            view.customUserAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X 14_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Safari/605.1.15"
        }
        if let zoom = tab?.pageZoom, abs(zoom - 1.0) > 0.01 {
            view.pageZoom = CGFloat(zoom)
        }
        webViews[id] = view

        // Lazy background tab restoration (Android v0.15.18 parity): only load immediately if requested or active!
        if (loadIfNeeded || id == activeID), let tab, tab.url != "about:blank", let url = URL(string: tab.url) {
            loadedTabIDs.insert(id)
            view.load(URLRequest(url: url))
        }
        return view
    }

    func applyUserScriptsToAllWebViews() {
        for (_, view) in webViews {
            let controller = view.configuration.userContentController
            controller.removeAllUserScripts()
            controller.removeAllContentRuleLists()
            if preferences.blockTrackers, let ruleList = compiledContentRuleList {
                controller.add(ruleList)
            }
            installUserScripts(into: controller)
        }
    }

    func rebuildAllWebViewsConfiguration() {
        let activeURL = currentURL
        webViews.removeAll()
        loadedTabIDs.removeAll()
        if activeURL != "about:blank" {
            _ = webView(for: activeID, loadIfNeeded: true)
        }
        objectWillChange.send()
    }

    private func installUserScripts(into controller: WKUserContentController) {
        // 1. Web Image Preloader (Android v0.15.17 / v0.15.18 exact parity!)
        if preferences.enableImagePreloader {
            let preloadStartJS = """
            (() => {
              if (window.__omyaruPreloadStartInstalled) return;
              window.__omyaruPreloadStartInstalled = true;
              const NativeIO = window.IntersectionObserver;
              if (typeof NativeIO === 'function') {
                const WrappedIO = function(callback, options) {
                  const opts = Object.assign({}, options || {});
                  if (!opts.root) {
                    opts.rootMargin = '1800px 600px 4500px 600px';
                    if (Array.isArray(opts.threshold) || Number(opts.threshold) > 0.01) {
                      opts.threshold = 0;
                    }
                  }
                  return new NativeIO(callback, opts);
                };
                WrappedIO.prototype = NativeIO.prototype;
                try { Object.defineProperty(window, 'IntersectionObserver', { value: WrappedIO, configurable: true, writable: true }); } catch (_) {}
              }
              window.lazySizesConfig = Object.assign(window.lazySizesConfig || {}, {
                expand: 2400,
                expFactor: 2.5,
                loadMode: 2
              });
            })();
            """
            controller.addUserScript(WKUserScript(source: preloadStartJS, injectionTime: .atDocumentStart, forMainFrameOnly: false))

            let preloadEndJS = """
            (() => {
              if (window.__omyaruPreloadEndInstalled) return;
              window.__omyaruPreloadEndInstalled = true;
              const lazyAttrs = ['data-original', 'data-src', 'data-lazy-src', 'data-lazy', 'data-url', 'data-echo', 'data-img-url'];
              const promoteNearViewport = () => {
                const topLimit = -800;
                const bottomLimit = window.innerHeight + 2200;
                for (const img of document.images) {
                  const rect = img.getBoundingClientRect();
                  if (rect.bottom < topLimit || rect.top > bottomLimit) continue;
                  if (img.loading === 'lazy') img.loading = 'eager';
                  if (img.decoding === 'async') img.decoding = 'auto';
                  const src = img.getAttribute('src') || '';
                  const isPlaceholder = !src || src.startsWith('data:') || /blank|pixel|spacer|loading|placeholder/i.test(src);
                  if (isPlaceholder) {
                    for (const attr of lazyAttrs) {
                      const candidate = img.getAttribute(attr);
                      if (candidate && candidate.length > 4 && !candidate.startsWith('data:')) {
                        img.src = candidate;
                        break;
                      }
                    }
                  }
                }
              };
              promoteNearViewport();
              let ticking = false;
              window.addEventListener('scroll', () => {
                if (ticking) return;
                ticking = true;
                requestAnimationFrame(() => { ticking = false; promoteNearViewport(); });
              }, { passive: true });
              setTimeout(promoteNearViewport, 450);
            })();
            """
            controller.addUserScript(WKUserScript(source: preloadEndJS, injectionTime: .atDocumentEnd, forMainFrameOnly: false))
        }

        // 2. Cosmetic Ad & Tracker Banner Hider
        if preferences.blockTrackers {
            let adHideJS = """
            (() => {
              const style = document.createElement('style');
              style.textContent = `
                ins.adsbygoogle, [data-ad-slot], [data-ad-client],
                iframe[src*="doubleclick"], iframe[src*="googlesyndication"],
                iframe[src*="adnxs"], iframe[src*="criteo"], iframe[src*="taboola"],
                [class*="ad-banner" i], [id*="ad-banner" i], [class*="ads-banner" i] {
                  display: none !important;
                }
              `;
              (document.head || document.documentElement)?.appendChild(style);
            })();
            """
            controller.addUserScript(WKUserScript(source: adHideJS, injectionTime: .atDocumentEnd, forMainFrameOnly: false))
        }

        // 3. Cookie Consent Notice Auto-Hider
        if preferences.hideCookieNotices {
            let cookieJS = """
            (() => {
              const style = document.createElement('style');
              style.textContent = `
                #onetrust-consent-sdk, .onetrust-pc-dark-filter,
                #CybotCookiebotDialog, #cookie-law-info-bar,
                .cc-window, .cc-banner, [id*="cookie-banner" i], [class*="cookie-banner" i],
                [id*="cookie-consent" i], [class*="cookie-consent" i] {
                  display: none !important;
                }
              `;
              (document.head || document.documentElement)?.appendChild(style);
            })();
            """
            controller.addUserScript(WKUserScript(source: cookieJS, injectionTime: .atDocumentEnd, forMainFrameOnly: false))
        }

        // 4. Right-click / Copy / Text Selection Unlocker
        if preferences.unlockCopyAndContextMenu {
            let unlockJS = """
            (() => {
              const style = document.createElement('style');
              style.textContent = '* { -webkit-user-select: auto !important; user-select: auto !important; -webkit-touch-callout: default !important; }';
              (document.head || document.documentElement)?.appendChild(style);
              for (const ev of ['copy', 'cut', 'contextmenu', 'selectstart', 'dragstart']) {
                window.addEventListener(ev, e => e.stopPropagation(), true);
              }
            })();
            """
            controller.addUserScript(WKUserScript(source: unlockJS, injectionTime: .atDocumentEnd, forMainFrameOnly: false))
        }

        // 5. Force Web Dark Mode
        if preferences.forceWebDarkMode {
            let darkJS = """
            (() => {
              const style = document.createElement('style');
              style.id = '__omyaru_force_dark';
              style.textContent = `
                html { filter: invert(0.92) hue-rotate(180deg) !important; background: #111 !important; }
                img, video, picture, canvas, iframe, svg { filter: invert(1) hue-rotate(180deg) !important; }
              `;
              (document.head || document.documentElement)?.appendChild(style);
            })();
            """
            controller.addUserScript(WKUserScript(source: darkJS, injectionTime: .atDocumentEnd, forMainFrameOnly: true))
        }

        // 6. Custom UserScripts (.user.js)
        for script in userScripts where script.isEnabled {
            controller.addUserScript(WKUserScript(
                source: script.code,
                injectionTime: script.runAtDocumentStart ? .atDocumentStart : .atDocumentEnd,
                forMainFrameOnly: false
            ))
        }
    }

    private func compileTrackerBlockerRules() {
        let rulesJSON = """
        [
          {
            "trigger": {
              "url-filter": ".*",
              "if-domain": ["*doubleclick.net", "*googlesyndication.com", "*google-analytics.com", "*adnxs.com", "*criteo.com", "*taboola.com", "*outbrain.com", "*scorecardresearch.com"]
            },
            "action": { "type": "block" }
          },
          {
            "trigger": {
              "url-filter": "https?://([^/]+\\\\.)?(doubleclick\\\\.net|googlesyndication\\\\.com|google-analytics\\\\.com|googletagmanager\\\\.com|adnxs\\\\.com|criteo\\\\.com|taboola\\\\.com|outbrain\\\\.com)/.*",
              "load-type": ["third-party"]
            },
            "action": { "type": "block" }
          }
        ]
        """
        WKContentRuleListStore.default().compileContentRuleList(
            forIdentifier: "OmyaruTrackerBlocker",
            encodedContentRuleList: rulesJSON
        ) { [weak self] ruleList, _ in
            guard let self, let ruleList else { return }
            Task { @MainActor in
                self.compiledContentRuleList = ruleList
                self.applyUserScriptsToAllWebViews()
            }
        }
    }

    // MARK: - Navigation & URL Resolution

    func open(_ rawEntry: String) {
        let value = rawEntry.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !value.isEmpty else { return }
        if value == "about:blank" {
            setTab(activeID) { $0.url = "about:blank"; $0.title = "새 탭" }
            return
        }

        let resolved: URL?
        if value.contains(" ") || (!value.contains(".") && !value.contains("://")) {
            resolved = preferences.searchEngine.searchURL(for: value)
        } else if value.contains("://") {
            if preferences.automaticHTTPS && value.lowercased().hasPrefix("http://") {
                resolved = URL(string: "https://" + value.dropFirst(7)) ?? URL(string: value)
            } else {
                resolved = URL(string: value)
            }
        } else {
            let scheme = preferences.automaticHTTPS ? "https://" : "http://"
            resolved = URL(string: scheme + value)
        }

        guard let url = resolved else { return }
        loadedTabIDs.insert(activeID)
        webView(for: activeID, loadIfNeeded: false).load(URLRequest(url: url))
        setTab(activeID) { $0.url = url.absoluteString }
    }

    func urlSuggestions(for input: String) -> [BrowserBookmark] {
        let query = input.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        guard !query.isEmpty else { return [] }
        var seen = Set<String>()
        var results: [BrowserBookmark] = []
        for bm in bookmarks where bm.title.lowercased().contains(query) || bm.url.lowercased().contains(query) {
            if seen.insert(bm.url.lowercased()).inserted {
                results.append(bm)
            }
            if results.count >= 6 { return results }
        }
        for h in history where h.title.lowercased().contains(query) || h.url.lowercased().contains(query) {
            if seen.insert(h.url.lowercased()).inserted {
                results.append(BrowserBookmark(title: h.title, url: h.url))
            }
            if results.count >= 6 { return results }
        }
        return results
    }

    // MARK: - Tab Operations (Including Duplicate Immediately After Source Tab!)

    @discardableResult
    func newTab(url: URL? = nil, afterTabID: UUID? = nil, background: Bool = false) -> UUID {
        let isPriv = activeGroupID == Self.privateGroupID
        let tab = BrowserTab(
            title: url?.host ?? "새 탭",
            url: url?.absoluteString ?? "about:blank",
            profileID: activeProfileID,
            groupID: activeGroupID,
            isPrivate: isPriv
        )
        if let anchorID = afterTabID, let anchorIdx = tabs.firstIndex(where: { $0.id == anchorID }) {
            tabs.insert(tab, at: anchorIdx + 1)
        } else {
            tabs.append(tab)
        }
        if !background {
            activeID = tab.id
        }
        if let url {
            loadedTabIDs.insert(tab.id)
            _ = webView(for: tab.id, loadIfNeeded: true)
        }
        persist()
        return tab.id
    }

    /// Duplicates `tabID` and inserts the duplicate IMMEDIATELY AFTER `tabID` (Android v0.15.17 parity!)
    func duplicateTab(_ tabID: UUID) {
        guard let source = tabs.first(where: { $0.id == tabID }),
              let sourceIdx = tabs.firstIndex(where: { $0.id == tabID }) else { return }
        let duplicate = BrowserTab(
            title: source.title,
            url: source.url,
            profileID: source.profileID,
            groupID: source.groupID,
            isPrivate: source.isPrivate,
            isDesktopMode: source.isDesktopMode,
            pageZoom: source.pageZoom
        )
        tabs.insert(duplicate, at: sourceIdx + 1)
        if let snap = tabSnapshots[tabID] {
            tabSnapshots[duplicate.id] = snap
        }
        activeID = duplicate.id
        persist()
        status = "탭을 바로 뒤에 복제했습니다."
    }

    func select(_ id: UUID) {
        guard let tab = tabs.first(where: { $0.id == id }) else { return }
        captureSnapshot(for: activeID)
        activeID = id
        if tab.isPrivate {
            activeGroupID = Self.privateGroupID
        } else {
            activeProfileID = tab.profileID
            activeGroupID = tab.groupID
        }
        _ = webView(for: id, loadIfNeeded: true)
        persist()
    }

    func close(_ id: UUID) {
        if let closingTab = tabs.first(where: { $0.id == id }), !closingTab.isPrivate, closingTab.url != "about:blank" {
            closedTabs.insert(closingTab, at: 0)
            if closedTabs.count > 25 { closedTabs.removeLast() }
        }
        webViews.removeValue(forKey: id)?.stopLoading()
        loadedTabIDs.remove(id)
        tabs.removeAll { $0.id == id }
        if visibleTabs.isEmpty {
            _ = newTab()
        } else if activeID == id {
            activeID = visibleTabs.last!.id
        }
        persist()
    }

    func closeOtherTabs(keeping keepID: UUID) {
        let toClose = visibleTabs.map(\.id).filter { $0 != keepID }
        for id in toClose {
            webViews.removeValue(forKey: id)?.stopLoading()
            loadedTabIDs.remove(id)
            tabs.removeAll { $0.id == id }
        }
        activeID = keepID
        persist()
    }

    func closeAllVisibleTabs() {
        let toClose = visibleTabs
        for tab in toClose {
            if !tab.isPrivate && tab.url != "about:blank" {
                closedTabs.insert(tab, at: 0)
            }
            webViews.removeValue(forKey: tab.id)?.stopLoading()
            loadedTabIDs.remove(tab.id)
        }
        let visibleIDs = Set(toClose.map(\.id))
        tabs.removeAll { visibleIDs.contains($0.id) }
        _ = newTab()
        persist()
    }

    func restoreClosedTab() {
        guard !closedTabs.isEmpty else {
            status = "복원할 닫은 탭이 없습니다."
            return
        }
        var restored = closedTabs.removeFirst()
        restored.profileID = activeProfileID
        restored.groupID = activeGroupID == Self.privateGroupID ? (profileGroups.first?.id ?? Self.defaultGroupID) : activeGroupID
        tabs.append(restored)
        activeID = restored.id
        _ = webView(for: restored.id, loadIfNeeded: true)
        persist()
        status = "닫은 탭을 복원했습니다."
    }

    func reorder(_ source: IndexSet, _ destination: Int) {
        let visible = visibleTabs
        var moved = visible
        moved.move(fromOffsets: source, toOffset: destination)
        let visibleSet = Set(visible.map(\.id))
        var iterator = moved.makeIterator()
        tabs = tabs.map { visibleSet.contains($0.id) ? iterator.next()! : $0 }
        persist()
    }

    func moveVisibleTab(fromID: UUID, toID: UUID) {
        guard fromID != toID,
              let fromIdx = tabs.firstIndex(where: { $0.id == fromID }),
              let toIdx = tabs.firstIndex(where: { $0.id == toID }) else { return }
        let item = tabs.remove(at: fromIdx)
        tabs.insert(item, at: toIdx)
        persist()
    }

    // MARK: - Profiles & Tab Groups

    func selectProfile(_ id: UUID) {
        guard profiles.contains(where: { $0.id == id }) else { return }
        activeProfileID = id
        if groups.filter({ $0.profileID == id }).isEmpty {
            groups.append(BrowserGroup(profileID: id, name: "기본"))
        }
        let targetGroup = groups.first(where: { $0.profileID == id })!.id
        selectGroup(targetGroup)
    }

    func addProfile(name: String, icon: String = "person.fill", colorHex: String = "#0A84FF") {
        let trimmed = name.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return }
        let profile = BrowserProfile(name: trimmed, icon: icon, colorHex: colorHex)
        let group = BrowserGroup(profileID: profile.id, name: "기본")
        profiles.append(profile)
        groups.append(group)
        selectProfile(profile.id)
    }

    func deleteProfile(_ id: UUID) {
        guard profiles.count > 1 else {
            status = "마지막 프로필은 삭제할 수 없습니다."
            return
        }
        let removingTabs = tabs.filter { $0.profileID == id }.map(\.id)
        for tid in removingTabs {
            webViews.removeValue(forKey: tid)?.stopLoading()
        }
        tabs.removeAll { $0.profileID == id }
        groups.removeAll { $0.profileID == id }
        profiles.removeAll { $0.id == id }
        selectProfile(profiles[0].id)
    }

    func selectGroup(_ id: UUID) {
        activeGroupID = id
        if let existing = visibleTabs.first {
            activeID = existing.id
            _ = webView(for: existing.id, loadIfNeeded: true)
        } else {
            _ = newTab()
        }
        persist()
    }

    func addGroup(_ name: String) {
        let trimmed = name.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return }
        let group = BrowserGroup(profileID: activeProfileID, name: trimmed)
        groups.append(group)
        selectGroup(group.id)
    }

    // MARK: - Page Controls, Find in Page, Zoom, Desktop Mode

    func back() { webViews[activeID]?.goBack() }
    func forward() { webViews[activeID]?.goForward() }
    func reload() { webViews[activeID]?.reload() }
    func home() {
        if preferences.homePage == "about:blank" || preferences.homePage.isEmpty {
            setTab(activeID) { $0.url = "about:blank"; $0.title = "새 탭" }
        } else {
            open(preferences.homePage)
        }
    }

    func toggleBookmark() {
        guard let tab = activeTab, tab.url != "about:blank" else { return }
        if let idx = bookmarks.firstIndex(where: { $0.url.caseInsensitiveCompare(tab.url) == .orderedSame }) {
            bookmarks.remove(at: idx)
            status = "북마크에서 제거됨"
        } else {
            bookmarks.append(BrowserBookmark(title: tab.title, url: tab.url))
            status = "북마크에 추가됨"
        }
        persist()
    }

    func toggleDesktopMode() {
        guard let tab = activeTab else { return }
        let next = !tab.isDesktopMode
        setTab(tab.id) { $0.isDesktopMode = next }
        if let view = webViews[tab.id] {
            view.customUserAgent = next
                ? "Mozilla/5.0 (Macintosh; Intel Mac OS X 14_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Safari/605.1.15"
                : nil
            view.reload()
        }
    }

    func setPageZoom(_ zoom: Double) {
        let clamped = min(2.0, max(0.5, zoom))
        setTab(activeID) { $0.pageZoom = clamped }
        webViews[activeID]?.pageZoom = CGFloat(clamped)
    }

    func findInPage(_ query: String, forward: Bool = true) async {
        findQuery = query
        guard let view = webViews[activeID] else { return }
        let trimmed = query.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else {
            _ = try? await view.evaluateJavaScript("window.getSelection()?.removeAllRanges();")
            findMatchCount = 0
            findCurrentIndex = 0
            return
        }
        let escaped = (try? JSONEncoder().encode(trimmed)).flatMap { String(data: $0, encoding: .utf8) } ?? "\"\""
        let dir = forward ? "false" : "true"
        let js = """
        (() => {
          const q = \(escaped);
          const text = document.body?.innerText || '';
          const matches = text.toLowerCase().split(q.toLowerCase()).length - 1;
          window.find(q, false, \(dir), true, false, true, false);
          return Math.max(0, matches);
        })();
        """
        if let count = try? await view.evaluateJavaScript(js) as? Int {
            findMatchCount = count
            if count > 0 {
                findCurrentIndex = forward
                    ? (findCurrentIndex % count) + 1
                    : ((findCurrentIndex - 2 + count) % count) + 1
            } else {
                findCurrentIndex = 0
            }
        }
    }

    func clearBrowsingData() async {
        let types = WKWebsiteDataStore.allWebsiteDataTypes()
        await WKWebsiteDataStore.default().removeData(ofTypes: types, modifiedSince: .distantPast)
        history.removeAll()
        persist()
        status = "모든 방문 기록·쿠키·캐시를 삭제했습니다."
    }

    // MARK: - WebKit Delegates

    private func setTab(_ id: UUID, change: (inout BrowserTab) -> Void) {
        guard let index = tabs.firstIndex(where: { $0.id == id }) else { return }
        change(&tabs[index])
        persist()
    }

    private func id(for webView: WKWebView) -> UUID? {
        webViews.first { $0.value === webView }?.key
    }

    func webView(_ webView: WKWebView, didStartProvisionalNavigation navigation: WKNavigation!) {
        if id(for: webView) == activeID {
            isLoading = true
            estimatedProgress = 0.15
        }
    }

    func webView(_ webView: WKWebView, didCommit navigation: WKNavigation!) {
        if id(for: webView) == activeID {
            estimatedProgress = max(0.45, webView.estimatedProgress)
        }
    }

    func webView(_ webView: WKWebView, didFinish navigation: WKNavigation!) {
        guard let id = id(for: webView) else { return }
        if id == activeID {
            isLoading = false
            estimatedProgress = 1.0
        }
        let url = webView.url?.absoluteString ?? "about:blank"
        let title = (webView.title?.isEmpty == false) ? webView.title! : url
        setTab(id) { tab in
            tab.url = url
            tab.title = title
        }
        if tabs.first(where: { $0.id == id })?.isPrivate != true && url != "about:blank" {
            history.removeAll { $0.url == url }
            history.insert(HistoryEntry(title: title, url: url), at: 0)
            persist()
        }
        captureSnapshot(for: id)
        objectWillChange.send()
    }

    func webView(_ webView: WKWebView, didFailProvisionalNavigation navigation: WKNavigation!, withError error: Error) {
        if id(for: webView) == activeID {
            isLoading = false
        }
        let nsErr = error as NSError
        if nsErr.code == NSURLErrorCancelled { return }
        status = error.localizedDescription
    }

    func webView(
        _ webView: WKWebView,
        createWebViewWith configuration: WKWebViewConfiguration,
        for navigationAction: WKNavigationAction,
        windowFeatures: WKWindowFeatures
    ) -> WKWebView? {
        if let url = navigationAction.request.url {
            if preferences.openLinksInNewTab {
                _ = newTab(url: url, afterTabID: id(for: webView))
            } else {
                webView.load(URLRequest(url: url))
            }
        }
        return nil
    }

    func webView(
        _ webView: WKWebView,
        decidePolicyFor navigationAction: WKNavigationAction,
        decisionHandler: @escaping (WKNavigationActionPolicy) -> Void
    ) {
        if let host = navigationAction.request.url?.host?.lowercased(),
           preferences.blockTrackers,
           (host.contains("doubleclick.net") || host.contains("googlesyndication.com") ||
            host.contains("adnxs.com") || host.contains("taboola.com")) {
            blockedTrackerCount += 1
            decisionHandler(.cancel)
            return
        }
        decisionHandler(navigationAction.shouldPerformDownload ? .download : .allow)
    }

    func webView(_ webView: WKWebView, navigationAction: WKNavigationAction, didBecome download: WKDownload) {
        download.delegate = self
    }

    func webView(_ webView: WKWebView, navigationResponse: WKNavigationResponse, didBecome download: WKDownload) {
        download.delegate = self
    }

    func download(
        _ download: WKDownload,
        decideDestinationUsing response: URLResponse,
        suggestedFilename: String,
        completionHandler: @escaping (URL?) -> Void
    ) {
        let directory = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("Downloads", isDirectory: true)
        do {
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
            let name = URL(fileURLWithPath: suggestedFilename).lastPathComponent
            var destination = directory.appendingPathComponent(name)
            if FileManager.default.fileExists(atPath: destination.path) {
                destination = directory.appendingPathComponent("\(Int(Date().timeIntervalSince1970))-" + name)
            }
            completionHandler(destination)
        } catch {
            completionHandler(nil)
            status = error.localizedDescription
        }
    }

    func downloadDidFinish(_ download: WKDownload) {
        refreshDownloadsList()
        status = "파일 다운로드 완료"
    }

    func refreshDownloadsList() {
        let directory = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("Downloads", isDirectory: true)
        downloads = (try? FileManager.default.contentsOfDirectory(at: directory, includingPropertiesForKeys: nil)) ?? []
    }
}
