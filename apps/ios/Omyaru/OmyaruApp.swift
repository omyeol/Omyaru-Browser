import SwiftUI
import WebKit
import UniformTypeIdentifiers
import UIKit
import AVFoundation

@main
struct OmyaruApp: App {
    @UIApplicationDelegateAdaptor(OmyaruAppDelegate.self) private var appDelegate
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

                if scenePhase != .active && (appLock.isEnabled || appLock.appSwitchBlackShield) {
                    Color.black
                        .ignoresSafeArea()
                        .overlay {
                            if !appLock.appSwitchBlackShield {
                                Image(systemName: "lock.shield.fill")
                                    .font(.system(size: 56))
                                    .foregroundStyle(.blue)
                            }
                        }
                }
            }
            .preferredColorScheme(browser.preferences.pageColorScheme.swiftUIColorScheme)
            .onAppear {
                OmyaruBackgroundSyncScheduler.shared.browserRef = browser
                OmyaruBackgroundSyncScheduler.shared.scheduleIfNeeded(
                    enabled: driveSync.backgroundSyncEnabled,
                    isConnected: driveSync.isConnected
                )
            }
            .onChange(of: scenePhase) { phase in
                switch phase {
                case .background:
                    appLock.lockIfNeeded()
                    browser.persist()
                    if browser.preferences.clearDataOnExit {
                        Task { await browser.clearBrowsingData() }
                    }
                    if driveSync.backgroundSyncEnabled && driveSync.isConnected {
                        OmyaruBackgroundSyncScheduler.shared.scheduleIfNeeded(enabled: true, isConnected: true)
                        if !driveSync.busy {
                            Task {
                                await driveSync.syncWithGoogleDrive(browser: browser, shelf: .shared, isBackground: true)
                            }
                        }
                    }
                case .active:
                    OmyaruBackgroundSyncScheduler.shared.browserRef = browser
                    if driveSync.autoSyncEnabled && driveSync.isConnected && !driveSync.busy {
                        Task {
                            await driveSync.syncWithGoogleDrive(browser: browser, shelf: .shared, isBackground: false)
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

// MARK: - Core Models (100% Android v0.15.18 Parity)

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
    var isPinned: Bool = false

    enum CodingKeys: String, CodingKey {
        case id, title, url, profileID, groupID, isPrivate, isDesktopMode, pageZoom, isPinned
    }

    init(
        id: UUID = UUID(),
        title: String = "새 탭",
        url: String = "about:blank",
        profileID: UUID,
        groupID: UUID,
        isPrivate: Bool = false,
        isDesktopMode: Bool = false,
        pageZoom: Double = 1.0,
        isPinned: Bool = false
    ) {
        self.id = id
        self.title = title
        self.url = url
        self.profileID = profileID
        self.groupID = groupID
        self.isPrivate = isPrivate
        self.isDesktopMode = isDesktopMode
        self.pageZoom = pageZoom
        self.isPinned = isPinned
    }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        id = try c.decodeIfPresent(UUID.self, forKey: .id) ?? UUID()
        title = try c.decodeIfPresent(String.self, forKey: .title) ?? "새 탭"
        url = try c.decodeIfPresent(String.self, forKey: .url) ?? "about:blank"
        profileID = try c.decode(UUID.self, forKey: .profileID)
        groupID = try c.decode(UUID.self, forKey: .groupID)
        isPrivate = try c.decodeIfPresent(Bool.self, forKey: .isPrivate) ?? false
        isDesktopMode = try c.decodeIfPresent(Bool.self, forKey: .isDesktopMode) ?? false
        pageZoom = try c.decodeIfPresent(Double.self, forKey: .pageZoom) ?? 1.0
        isPinned = try c.decodeIfPresent(Bool.self, forKey: .isPinned) ?? false
    }
}

struct BookmarkFolder: Codable, Identifiable, Hashable {
    var id: String = UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased()
    var name: String
    var parentID: String? = nil
}

struct BrowserBookmark: Codable, Identifiable, Hashable {
    var id: UUID = UUID()
    var title: String
    var url: String
    var folderID: String? = nil
}

struct HistoryEntry: Codable, Identifiable, Hashable {
    var id: UUID = UUID()
    var title: String
    var url: String
    var visitedAtEpochMs: Int64 = Int64(Date().timeIntervalSince1970 * 1000)
}

struct PendingDownloadPrompt: Identifiable {
    let id = UUID()
    let suggestedFilename: String
    let mimeType: String
    let isImage: Bool
    let tempFileURL: URL
}

struct UrlSuggestionRecord: Codable, Hashable {
    var url: String
    var title: String
    var visits: Int
    var lastVisit: Double
}

struct WebContextElement: Identifiable {
    enum Kind: String {
        case image
        case video
        case link
    }
    let id = UUID()
    let kind: Kind
    let mediaURL: String?
    let linkURL: String?
    let title: String
}

private final class WeakScriptMessageHandler: NSObject, WKScriptMessageHandler {
    weak var owner: BrowserModel?
    init(owner: BrowserModel) {
        self.owner = owner
    }
    func userContentController(_ userContentController: WKUserContentController, didReceive message: WKScriptMessage) {
        let name = message.name
        let body = message.body
        Task { @MainActor [weak owner] in
            owner?.handleScriptMessage(name: name, body: body)
        }
    }
}

// MARK: - Settings Enums (Exact Android BrowserSettings.kt Parity)

enum SearchEngineChoice: String, CaseIterable, Codable, Identifiable {
    case google = "Google"
    case duckduckgo = "DuckDuckGo"
    case brave = "Brave Search"
    case startpage = "Startpage"
    case naver = "Naver"
    case bing = "Bing"
    case custom = "사용자 지정"

    var id: String { rawValue }

    func searchURL(for query: String, customTemplate: String) -> URL? {
        let encoded = query.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed) ?? query
        switch self {
        case .google: return URL(string: "https://www.google.com/search?q=\(encoded)")
        case .duckduckgo: return URL(string: "https://duckduckgo.com/?q=\(encoded)")
        case .brave: return URL(string: "https://search.brave.com/search?q=\(encoded)")
        case .startpage: return URL(string: "https://www.startpage.com/sp/search?query=\(encoded)")
        case .naver: return URL(string: "https://search.naver.com/search.naver?query=\(encoded)")
        case .bing: return URL(string: "https://www.bing.com/search?q=\(encoded)")
        case .custom:
            let tpl = customTemplate.contains("%s") ? customTemplate : "https://duckduckgo.com/?q=%s"
            return URL(string: tpl.replacingOccurrences(of: "%s", with: encoded))
        }
    }
}

enum TrackingProtectionLevel: String, CaseIterable, Codable, Identifiable {
    case off = "끄기"
    case standard = "표준"
    case strict = "엄격"
    var id: String { rawValue }
}

enum CookiePolicyLevel: String, CaseIterable, Codable, Identifiable {
    case isolateCrossSite = "교차 사이트 쿠키 격리"
    case firstPartyOnly = "자사 쿠키만 허용"
    case blockAll = "모든 쿠키 차단"
    var id: String { rawValue }
}

enum PageColorSchemeChoice: String, CaseIterable, Codable, Identifiable {
    case system = "시스템 설정"
    case light = "라이트 모드"
    case dark = "다크 모드"
    var id: String { rawValue }

    var swiftUIColorScheme: ColorScheme? {
        switch self {
        case .system: return nil
        case .light: return .light
        case .dark: return .dark
        }
    }
}

enum AddressBarPositionChoice: String, CaseIterable, Codable, Identifiable {
    case bottom = "하단"
    case top = "상단"
    var id: String { rawValue }
}

enum TabAspectRatioChoice: String, CaseIterable, Codable, Identifiable {
    case square = "1:1"
    case portrait = "3:4"
    case wide = "16:9"
    var id: String { rawValue }

    var heightRatio: CGFloat {
        switch self {
        case .square: return 1.0
        case .portrait: return 4.0 / 3.0
        case .wide: return 9.0 / 16.0
        }
    }
}

/// Exact 20-item parity with Android `ToolbarShortcuts.kt`!
enum ToolbarShortcutAction: String, CaseIterable, Codable, Identifiable {
    case back = "back"
    case forward = "forward"
    case home = "home"
    case newTab = "new_tab"
    case tabs = "tabs"
    case menu = "menu"
    case findInPage = "find"
    case contentBlocking = "blocking"
    case desktopSite = "desktop"
    case extensions = "extensions"
    case capture = "capture"
    case watermark = "watermark"
    case bookshelf = "bookshelf"
    case vpn = "vpn"
    case history = "history"
    case bookmarks = "bookmarks"
    case share = "share"
    case copyAddress = "copy"
    case reopenClosedTab = "restore"
    case closeAllTabs = "close_all"
    case settings = "settings"

    var id: String { rawValue }

    var title: String {
        switch self {
        case .back: return "뒤로"
        case .forward: return "앞으로"
        case .home: return "홈"
        case .newTab: return "새 탭"
        case .tabs: return "탭 목록"
        case .menu: return "메뉴"
        case .findInPage: return "페이지에서 찾기"
        case .contentBlocking: return "콘텐츠 차단"
        case .desktopSite: return "데스크톱 사이트"
        case .extensions: return "확장 프로그램"
        case .capture: return "화면 캡처"
        case .watermark: return "워터마크 삭제"
        case .bookshelf: return "책장"
        case .vpn: return "VPN · 보안 DNS"
        case .history: return "방문 기록"
        case .bookmarks: return "북마크"
        case .share: return "공유"
        case .copyAddress: return "주소 복사"
        case .reopenClosedTab: return "닫은 탭 복원"
        case .closeAllTabs: return "모든 탭 닫기"
        case .settings: return "설정"
        }
    }

    var systemImage: String {
        switch self {
        case .back: return "chevron.left"
        case .forward: return "chevron.right"
        case .home: return "house"
        case .newTab: return "plus"
        case .tabs: return "square.on.square"
        case .menu: return "ellipsis.circle"
        case .findInPage: return "magnifyingglass"
        case .contentBlocking: return "shield.lefthalf.filled"
        case .desktopSite: return "desktopcomputer"
        case .extensions: return "puzzlepiece.extension"
        case .capture: return "viewfinder"
        case .watermark: return "wand.and.stars"
        case .bookshelf: return "books.vertical"
        case .vpn: return "network.badge.shield.half.filled"
        case .history: return "clock"
        case .bookmarks: return "book"
        case .share: return "square.and.arrow.up"
        case .copyAddress: return "doc.on.doc"
        case .reopenClosedTab: return "arrow.uturn.backward"
        case .closeAllTabs: return "xmark.square"
        case .settings: return "gearshape"
        }
    }

    static let defaultOrder: [ToolbarShortcutAction] = [.back, .forward, .home, .capture, .bookshelf, .tabs, .menu]
}

struct BrowserPreferences: Codable, Equatable {
    // General
    var searchEngine: SearchEngineChoice = .google
    var customSearchTemplate: String = "https://duckduckgo.com/?q=%s"
    var homePage: String = "about:blank"
    var pipSeekSeconds: Int = 10

    // Appearance
    var pageColorScheme: PageColorSchemeChoice = .dark
    var liquidGlassUi: Bool = false
    var addressBarPosition: AddressBarPositionChoice = .bottom
    var hideToolbarOnScroll: Bool = true
    var expandToolbarOnScrollUp: Bool = true
    var captureSideMargins: Bool = true
    var fontScale: Double = 1.0
    var compactToolbar: Bool = false
    var toolbarShortcuts: [ToolbarShortcutAction] = ToolbarShortcutAction.defaultOrder

    // Tabs
    var tabThumbnailAspectRatio: TabAspectRatioChoice = .portrait
    var tabGridColumns: Int = 2
    var tabsStartFromBottom: Bool = true
    var showKeyboardOnNewTab: Bool = false
    var keepKeyboardAcrossTabSwitch: Bool = false
    var restoreTabs: Bool = true
    var openLinksInNewTab: Bool = true
    var showTabThumbnails: Bool = true

    // Privacy & Security
    var trackingProtection: TrackingProtectionLevel = .strict
    var cookiePolicy: CookiePolicyLevel = .isolateCrossSite
    var globalPrivacyControl: Bool = true
    var automaticHTTPS: Bool = true
    var clearDataOnExit: Bool = false
    var blockPopups: Bool = true
    var contentBlockingExceptions: [String] = []

    // Advanced & Extensions
    var showFullURL: Bool = true
    var backgroundAudio: Bool = true
    var javaScriptEnabled: Bool = true
    var desktopModeDefault: Bool = false
    var watermarkRemoverUrl: String = ""
    var enableImagePreloader: Bool = true
    var hideCookieNotices: Bool = true
    var unlockCopyAndContextMenu: Bool = true
    var enableMediaPiPHelper: Bool = true
    var forceWebDarkMode: Bool = false

    // DNS & Proxy
    var dnsPreset: DnsProviderPreset = .system
    var customDoHURL: String = "https://cloudflare-dns.com/dns-query"
    var proxyEnabled: Bool = false
    var proxyIsSOCKS5: Bool = true
    var proxyHost: String = "127.0.0.1"
    var proxyPort: Int = 1080
}

// MARK: - Main Browser ViewModel & WebKit Controller

@MainActor
final class BrowserModel: NSObject, ObservableObject, WKNavigationDelegate, WKUIDelegate, WKDownloadDelegate, UIScrollViewDelegate {
    static let privateGroupID = UUID(uuidString: "00000000-0000-0000-0000-000000000001")!
    static let defaultProfileID = UUID(uuidString: "00000000-0000-0000-0000-000000000010")!
    static let defaultGroupID = UUID(uuidString: "00000000-0000-0000-0000-000000000002")!
    static let appVersionName = "0.15.18"
    static let appBuildNumber = 80

    @Published var profiles: [BrowserProfile]
    @Published var groups: [BrowserGroup]
    @Published var tabs: [BrowserTab]
    @Published var closedTabs: [BrowserTab] = []
    @Published var activeProfileID: UUID
    @Published var activeGroupID: UUID
    @Published var activeID: UUID
    @Published var bookmarkFolders: [BookmarkFolder]
    @Published var bookmarks: [BrowserBookmark]
    @Published var history: [HistoryEntry]
    @Published var userScripts: [UserScriptExtension]
    @Published var wireGuardProfiles: [WireGuardProfile]
    @Published var preferences: BrowserPreferences
    @Published var downloads: [URL] = []
    @Published var pendingDownload: PendingDownloadPrompt? = nil
    @Published var tabSnapshots: [UUID: UIImage] = [:]
    @Published var blockedTrackerCount: Int = 0
    @Published var estimatedProgress: Double = 0.0
    @Published var isLoading: Bool = false
    @Published var isChromeCollapsed: Bool = false
    @Published var failedLoadURL: String? = nil
    @Published var failedLoadMessage: String? = nil
    @Published var scrollFraction: Double = 0.0
    @Published var scrollViewportRatio: Double = 1.0
    @Published var isScrollable: Bool = false
    @Published var lastScrollActivity: Date = .distantPast
    @Published var activeContextElement: WebContextElement? = nil
    @Published var findQuery: String = ""
    @Published var findMatchCount: Int = 0
    @Published var findCurrentIndex: Int = 0
    @Published var status: String = "" {
        didSet {
            guard !status.isEmpty else { return }
            statusDismissTask?.cancel()
            statusDismissTask = Task { @MainActor [weak self] in
                try? await Task.sleep(nanoseconds: 2_800_000_000)
                guard !Task.isCancelled else { return }
                self?.status = ""
            }
        }
    }

    private var webViews: [UUID: WKWebView] = [:]
    private var loadedTabIDs: Set<UUID> = []
    private var compiledContentRuleList: WKContentRuleList? = nil
    private var lastScrollOffsetY: CGFloat = 0
    private var toolbarScrollAccumulator: CGFloat = 0
    private var visitRecordsByProfile: [UUID: [UrlSuggestionRecord]] = [:]
    private var activeDownloadTempInfo: (url: URL, suggestedName: String, mimeType: String)? = nil
    private var statusDismissTask: Task<Void, Never>? = nil

    private static let stateKey = "omyaru.ios.browser.v3"
    private static let prefsKey = "omyaru.ios.preferences.v3"
    private static let visitsKey = "omyaru.ios.url_suggestions.v1"

    private struct PersistedState: Codable {
        var profiles: [BrowserProfile]
        var groups: [BrowserGroup]
        var tabs: [BrowserTab]
        var activeProfileID: UUID
        var activeGroupID: UUID
        var activeID: UUID
        var bookmarkFolders: [BookmarkFolder]
        var bookmarks: [BrowserBookmark]
        var history: [HistoryEntry]
        var userScripts: [UserScriptExtension]
        var wireGuardProfiles: [WireGuardProfile]
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
            let regularTabs = savedPrefs.restoreTabs ? saved.tabs.filter { !$0.isPrivate } : []
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
            wireGuardProfiles = saved.wireGuardProfiles
            blockedTrackerCount = saved.blockedTrackerCount
        } else {
            let initialTab = BrowserTab(profileID: Self.defaultProfileID, groupID: Self.defaultGroupID)
            profiles = [defaultProfile]
            groups = [defaultGroup]
            tabs = [initialTab]
            activeProfileID = Self.defaultProfileID
            activeGroupID = Self.defaultGroupID
            activeID = initialTab.id
            bookmarkFolders = [BookmarkFolder(name: "즐겨찾기"), BookmarkFolder(name: "읽을거리")]
            bookmarks = defaultBookmarks
            history = []
            userScripts = []
            wireGuardProfiles = []
            blockedTrackerCount = 0
        }
        super.init()
        if let vData = UserDefaults.standard.data(forKey: Self.visitsKey),
           let decodedVisits = try? JSONDecoder().decode([String: [UrlSuggestionRecord]].self, from: vData) {
            var map: [UUID: [UrlSuggestionRecord]] = [:]
            for (k, v) in decodedVisits {
                if let uuid = UUID(uuidString: k) { map[uuid] = v }
            }
            visitRecordsByProfile = map
        }
        configureAudioSession()
        refreshDownloadsList()
        loadPersistedSnapshotsAsync()
        compileTrackerBlockerRules()
    }

    func configureAudioSession() {
        let session = AVAudioSession.sharedInstance()
        if preferences.backgroundAudio {
            try? session.setCategory(.playback, mode: .default, options: [.mixWithOthers])
            try? session.setActive(true)
        } else {
            try? session.setCategory(.ambient, mode: .default, options: [.mixWithOthers])
        }
    }

    // MARK: - Computed Properties

    var activeTab: BrowserTab? { tabs.first { $0.id == activeID } }
    var activeProfile: BrowserProfile { profiles.first { $0.id == activeProfileID } ?? profiles[0] }
    var currentURL: String { activeTab?.url ?? "about:blank" }
    var currentHost: String { URL(string: currentURL)?.host?.lowercased() ?? "" }
    var isStartPage: Bool { currentURL == "about:blank" || currentURL.isEmpty }
    var canGoBack: Bool { webViews[activeID]?.canGoBack ?? false }
    var canGoForward: Bool { webViews[activeID]?.canGoForward ?? false }

    var isCurrentBookmarked: Bool {
        guard !isStartPage else { return false }
        return bookmarks.contains { $0.url.caseInsensitiveCompare(currentURL) == .orderedSame }
    }

    var isContentBlockingDisabledForCurrentHost: Bool {
        let host = currentHost
        guard !host.isEmpty else { return false }
        return preferences.contentBlockingExceptions.contains(host)
    }

    var profileGroups: [BrowserGroup] {
        groups.filter { $0.profileID == activeProfileID }
    }

    var workspaceGroupIDs: [UUID] {
        [Self.privateGroupID] + profileGroups.map(\.id)
    }

    var activeGroupDisplayName: String {
        if activeGroupID == Self.privateGroupID { return "비공개" }
        if let g = groups.first(where: { $0.id == activeGroupID }) {
            return g.name == "기본" ? activeProfile.name : g.name
        }
        return activeProfile.name
    }

    var collapsedPillTitle: String {
        if let t = activeTab?.title.trimmingCharacters(in: .whitespacesAndNewlines),
           !t.isEmpty, t != "새 탭" {
            return t
        }
        let host = URL(string: currentURL)?.host?.replacingOccurrences(of: "^www\\.", with: "", options: .regularExpression)
        return host ?? currentURL
    }

    var visibleTabs: [BrowserTab] {
        tabs(forGroup: activeGroupID)
    }

    func tabs(forGroup groupID: UUID) -> [BrowserTab] {
        let raw: [BrowserTab]
        if groupID == Self.privateGroupID {
            raw = tabs.filter { $0.isPrivate }
        } else {
            raw = tabs.filter { !$0.isPrivate && $0.profileID == activeProfileID && $0.groupID == groupID }
        }
        let pinned = raw.filter { $0.isPinned }
        let unpinned = raw.filter { !$0.isPinned }
        return pinned + unpinned
    }

    func groupDisplayName(for groupID: UUID) -> String {
        if groupID == Self.privateGroupID {
            return "개인정보 보호"
        }
        if let firstGroup = profileGroups.first, groupID == firstGroup.id {
            let count = tabs(forGroup: groupID).count
            return "\(count)개의 탭"
        }
        if let g = groups.first(where: { $0.id == groupID }) {
            return g.name
        }
        return activeProfile.name
    }

    func groupHeaderTitle(for groupID: UUID) -> String? {
        if groupID == Self.privateGroupID { return "개인정보 보호" }
        if let firstGroup = profileGroups.first, groupID == firstGroup.id { return nil }
        return groups.first(where: { $0.id == groupID })?.name
    }

    func togglePinTab(_ tabID: UUID) {
        guard let idx = tabs.firstIndex(where: { $0.id == tabID }) else { return }
        tabs[idx].isPinned.toggle()
        let pinnedNow = tabs[idx].isPinned
        persist()
        status = pinnedNow ? "탭을 고정했습니다." : "탭 고정을 해제했습니다."
    }

    func bookmarkAllVisibleTabs() {
        let valid = visibleTabs.filter { !$0.url.isEmpty && $0.url != "about:blank" }
        guard !valid.isEmpty else {
            status = "북마크에 추가할 페이지가 없습니다."
            return
        }
        let folderName = groupHeaderTitle(for: activeGroupID) ?? "\(activeProfile.name) 탭"
        let folderID: String
        if let existing = bookmarkFolders.first(where: { $0.name.caseInsensitiveCompare(folderName) == .orderedSame }) {
            folderID = existing.id
        } else {
            let created = BookmarkFolder(name: folderName)
            bookmarkFolders.append(created)
            folderID = created.id
        }
        var added = 0
        for t in valid {
            if !bookmarks.contains(where: { $0.url.caseInsensitiveCompare(t.url) == .orderedSame && $0.folderID == folderID }) {
                bookmarks.append(BrowserBookmark(title: t.title, url: t.url, folderID: folderID))
                added += 1
            }
        }
        persist()
        status = added > 0 ? "'\(folderName)' 폴더에 \(added)개 탭을 북마크했습니다." : "이미 모든 탭이 북마크되어 있습니다."
    }

    func activeWebView() -> WKWebView? {
        guard let tab = activeTab else { return nil }
        return webView(for: tab.id)
    }

    // MARK: - Persistence & Cache Management

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
            wireGuardProfiles: wireGuardProfiles,
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
        updateAllWebViewContentInsets()
    }

    func resetAllPreferences() {
        preferences = BrowserPreferences()
        savePreferences()
        applyUserScriptsToAllWebViews()
        status = "모든 설정을 기본값으로 초기화했습니다."
    }

    private var snapshotsDirectory: URL {
        let dir = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("TabSnapshots", isDirectory: true)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        return dir
    }

    func clearTabPreviews() {
        tabSnapshots.removeAll()
        if let files = try? FileManager.default.contentsOfDirectory(at: snapshotsDirectory, includingPropertiesForKeys: nil) {
            for f in files { try? FileManager.default.removeItem(at: f) }
        }
        status = "저장된 모든 탭 미리보기 이미지를 삭제했습니다."
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

    // MARK: - Per-Site Content Blocking Toggle (Android PageToolsDialog Parity!)

    func toggleContentBlockingForCurrentHost() {
        let host = currentHost
        guard !host.isEmpty else { return }
        if let idx = preferences.contentBlockingExceptions.firstIndex(of: host) {
            preferences.contentBlockingExceptions.remove(at: idx)
            status = "\(host) 콘텐츠 차단을 다시 켰습니다."
        } else {
            preferences.contentBlockingExceptions.append(host)
            status = "\(host) 사이트에서 콘텐츠 차단을 껐습니다."
        }
        savePreferences()
        applyUserScriptsToAllWebViews()
        webViews[activeID]?.reload()
    }

    func clearContentBlockingExceptions() {
        preferences.contentBlockingExceptions.removeAll()
        savePreferences()
        applyUserScriptsToAllWebViews()
        status = "모든 사이트별 콘텐츠 차단 예외를 초기화했습니다."
    }

    // MARK: - WebKit Engine (Lazy Restore, Image Preloader, GPC, Scroll Toolbar Hide/Show)

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
        config.websiteDataStore = tab?.isPrivate == true ? .nonPersistent() : .default()
        config.allowsInlineMediaPlayback = true
        config.allowsPictureInPictureMediaPlayback = true
        config.defaultWebpagePreferences.allowsContentJavaScript = preferences.javaScriptEnabled
        config.preferences.javaScriptCanOpenWindowsAutomatically = !preferences.blockPopups

        let controller = WKUserContentController()
        controller.add(WeakScriptMessageHandler(owner: self), name: "omyaruContext")
        let host = URL(string: tab?.url ?? "")?.host?.lowercased() ?? ""
        let blockingEnabled = preferences.trackingProtection != .off && !preferences.contentBlockingExceptions.contains(host)
        if blockingEnabled, let ruleList = compiledContentRuleList {
            controller.add(ruleList)
        }
        installUserScripts(into: controller, blockingEnabled: blockingEnabled)
        config.userContentController = controller

        let view = WKWebView(frame: .zero, configuration: config)
        view.allowsBackForwardNavigationGestures = true
        view.navigationDelegate = self
        view.uiDelegate = self
        view.scrollView.delegate = self
        view.scrollView.showsVerticalScrollIndicator = false
        view.isOpaque = false
        view.backgroundColor = UIColor(red: 0.09, green: 0.075, blue: 0.067, alpha: 1.0)
        applyContentInsets(to: view)
        if tab?.isDesktopMode == true || preferences.desktopModeDefault {
            view.customUserAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X 14_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Safari/605.1.15"
        }
        let zoom = tab?.pageZoom ?? preferences.fontScale
        if abs(zoom - 1.0) > 0.01 {
            view.pageZoom = CGFloat(zoom)
        }
        webViews[id] = view

        if (loadIfNeeded || id == activeID), let tab, tab.url != "about:blank", let url = URL(string: tab.url) {
            loadedTabIDs.insert(id)
            view.load(URLRequest(url: url))
        }
        return view
    }

    func applyContentInsets(to webView: WKWebView) {
        let bottomInset: CGFloat
        let topInset: CGFloat
        if preferences.addressBarPosition == .bottom {
            topInset = 0
            bottomInset = isChromeCollapsed ? 58 : 126
        } else {
            topInset = isChromeCollapsed ? 56 : 124
            bottomInset = 0
        }
        if abs(webView.scrollView.contentInset.bottom - bottomInset) > 1 ||
           abs(webView.scrollView.contentInset.top - topInset) > 1 {
            webView.scrollView.contentInset = UIEdgeInsets(top: topInset, left: 0, bottom: bottomInset, right: 0)
            webView.scrollView.verticalScrollIndicatorInsets = webView.scrollView.contentInset
        }
    }

    func updateAllWebViewContentInsets() {
        for view in webViews.values {
            applyContentInsets(to: view)
        }
    }

    func applyUserScriptsToAllWebViews() {
        configureAudioSession()
        for (tid, view) in webViews {
            let tab = tabs.first(where: { $0.id == tid })
            let host = URL(string: tab?.url ?? "")?.host?.lowercased() ?? ""
            let blockingEnabled = preferences.trackingProtection != .off && !preferences.contentBlockingExceptions.contains(host)
            view.configuration.defaultWebpagePreferences.allowsContentJavaScript = preferences.javaScriptEnabled
            view.configuration.preferences.javaScriptCanOpenWindowsAutomatically = !preferences.blockPopups
            let controller = view.configuration.userContentController
            controller.removeAllUserScripts()
            controller.removeAllContentRuleLists()
            if blockingEnabled, let ruleList = compiledContentRuleList {
                controller.add(ruleList)
            }
            installUserScripts(into: controller, blockingEnabled: blockingEnabled)
            view.pageZoom = CGFloat(tab?.pageZoom ?? preferences.fontScale)
            applyContentInsets(to: view)
        }
    }

    func scrollToFraction(_ fraction: Double) {
        guard let view = webViews[activeID] else { return }
        let sv = view.scrollView
        let viewportH = max(1, sv.bounds.height - sv.adjustedContentInset.top - sv.adjustedContentInset.bottom)
        let maxY = max(0, sv.contentSize.height - viewportH)
        let targetY = -sv.adjustedContentInset.top + CGFloat(min(1, max(0, fraction))) * maxY
        sv.setContentOffset(CGPoint(x: sv.contentOffset.x, y: targetY), animated: false)
        scrollFraction = min(1, max(0, fraction))
        lastScrollActivity = Date()
    }

    func scrollViewDidScroll(_ scrollView: UIScrollView) {
        let viewportH = max(1, scrollView.bounds.height)
        let contentH = scrollView.contentSize.height
        let scrollable = contentH - viewportH
        if scrollable > 40 {
            let adjustedY = max(0, scrollView.contentOffset.y + scrollView.adjustedContentInset.top)
            isScrollable = true
            scrollViewportRatio = min(1.0, max(0.12, Double(viewportH / max(contentH, 1))))
            scrollFraction = min(1.0, max(0.0, Double(adjustedY / max(scrollable, 1))))
            lastScrollActivity = Date()
        } else {
            isScrollable = false
        }

        guard preferences.hideToolbarOnScroll else {
            if isChromeCollapsed {
                isChromeCollapsed = false
                updateAllWebViewContentInsets()
            }
            return
        }
        let y = scrollView.contentOffset.y + scrollView.adjustedContentInset.top
        let delta = y - lastScrollOffsetY
        lastScrollOffsetY = y
        if y <= 8 {
            toolbarScrollAccumulator = 0
            if preferences.expandToolbarOnScrollUp && isChromeCollapsed {
                isChromeCollapsed = false
                updateAllWebViewContentInsets()
            }
            return
        }
        if delta == 0 || abs(delta) > viewportH { return }

        if delta > 0 {
            if toolbarScrollAccumulator < 0 { toolbarScrollAccumulator = 0 }
            toolbarScrollAccumulator += delta
            if !isChromeCollapsed && toolbarScrollAccumulator >= 24 {
                toolbarScrollAccumulator = 0
                isChromeCollapsed = true
                updateAllWebViewContentInsets()
            }
        } else {
            guard preferences.expandToolbarOnScrollUp else {
                toolbarScrollAccumulator = 0
                return
            }
            if toolbarScrollAccumulator > 0 { toolbarScrollAccumulator = 0 }
            toolbarScrollAccumulator += delta
            if isChromeCollapsed && toolbarScrollAccumulator <= -14 {
                toolbarScrollAccumulator = 0
                isChromeCollapsed = false
                updateAllWebViewContentInsets()
            }
        }
    }

    func handleScriptMessage(name: String, body: Any) {
        guard name == "omyaruContext", let dict = body as? [String: Any] else { return }
        let kindStr = (dict["kind"] as? String) ?? "link"
        let mediaURL = (dict["mediaUrl"] as? String)?.trimmingCharacters(in: .whitespacesAndNewlines)
        let linkURL = (dict["linkUrl"] as? String)?.trimmingCharacters(in: .whitespacesAndNewlines)
        let title = ((dict["title"] as? String)?.trimmingCharacters(in: .whitespacesAndNewlines)).flatMap { $0.isEmpty ? nil : $0 }
            ?? mediaURL ?? linkURL ?? "항목"
        let kind: WebContextElement.Kind = kindStr == "image" ? .image : (kindStr == "video" ? .video : .link)
        guard (mediaURL?.isEmpty == false) || (linkURL?.isEmpty == false) else { return }
        UIImpactFeedbackGenerator(style: .medium).impactOccurred()
        activeContextElement = WebContextElement(
            kind: kind,
            mediaURL: mediaURL?.isEmpty == true ? nil : mediaURL,
            linkURL: linkURL?.isEmpty == true ? nil : linkURL,
            title: String(title.prefix(64))
        )
    }

    private func installUserScripts(into controller: WKUserContentController, blockingEnabled: Bool) {
        // 0. Global Privacy Control (GPC) Header/Navigator API
        if preferences.globalPrivacyControl {
            let gpcJS = """
            (() => {
              try {
                Object.defineProperty(Navigator.prototype, 'globalPrivacyControl', { get: () => true, configurable: true });
              } catch (_) {}
            })();
            """
            controller.addUserScript(WKUserScript(source: gpcJS, injectionTime: .atDocumentStart, forMainFrameOnly: false))
        }

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
        if blockingEnabled {
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

        // 7. Android-Style Long-Press Context Menu Bridge (Images / Videos / Links -> Save to Bookshelf, Background Tab, Copy, Download)
        let contextBridgeJS = """
        (() => {
          if (window.__omyaruContextInstalled) return;
          window.__omyaruContextInstalled = true;
          let timer = null, startX = 0, startY = 0;
          const findInfo = (el) => {
            if (!el) return null;
            const img = el.closest ? el.closest('img') : null;
            const vid = el.closest ? el.closest('video, audio') : null;
            const link = el.closest ? el.closest('a[href]') : null;
            const mediaUrl = img ? (img.currentSrc || img.src || '') : (vid ? (vid.currentSrc || vid.src || (vid.querySelector('source') ? vid.querySelector('source').src : '')) : '');
            const linkUrl = link ? link.href : '';
            if (!mediaUrl && !linkUrl) return null;
            const kind = img ? 'image' : (vid ? 'video' : 'link');
            const title = (img && (img.alt || img.title)) || (link && (link.innerText || link.title)) || mediaUrl || linkUrl;
            return { kind, mediaUrl, linkUrl, title: (title || '').trim().slice(0, 80) };
          };
          const cancel = () => { if (timer) { clearTimeout(timer); timer = null; } };
          document.addEventListener('touchstart', (e) => {
            if (e.touches.length !== 1) { cancel(); return; }
            const t = e.touches[0];
            startX = t.clientX; startY = t.clientY;
            const target = e.target;
            const info = findInfo(target);
            if (!info) return;
            cancel();
            timer = setTimeout(() => {
              timer = null;
              try { window.webkit.messageHandlers.omyaruContext.postMessage(info); } catch (_) {}
            }, 520);
          }, { passive: true });
          document.addEventListener('touchmove', (e) => {
            if (!timer || !e.touches.length) return;
            const t = e.touches[0];
            if (Math.abs(t.clientX - startX) > 10 || Math.abs(t.clientY - startY) > 10) cancel();
          }, { passive: true });
          document.addEventListener('touchend', cancel, { passive: true });
          document.addEventListener('touchcancel', cancel, { passive: true });
        })();
        """
        controller.addUserScript(WKUserScript(source: contextBridgeJS, injectionTime: .atDocumentEnd, forMainFrameOnly: false))
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

    // MARK: - Navigation & URL Suggestions

    func open(_ rawEntry: String) {
        let value = rawEntry.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !value.isEmpty else { return }
        isChromeCollapsed = false
        if value == "about:blank" {
            setTab(activeID) { $0.url = "about:blank"; $0.title = "새 탭" }
            return
        }

        let resolved: URL?
        if value.contains(" ") || (!value.contains(".") && !value.contains("://")) {
            resolved = preferences.searchEngine.searchURL(for: value, customTemplate: preferences.customSearchTemplate)
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

    func recordVisitSuggestion(profileID: UUID, url: String, title: String) {
        guard url.lowercased().hasPrefix("http://") || url.lowercased().hasPrefix("https://") else { return }
        var rows = visitRecordsByProfile[profileID] ?? []
        let now = Date().timeIntervalSince1970
        if let idx = rows.firstIndex(where: { $0.url.caseInsensitiveCompare(url) == .orderedSame }) {
            var item = rows.remove(at: idx)
            item.title = title.isEmpty ? item.title : title
            item.visits += 1
            item.lastVisit = now
            rows.insert(item, at: 0)
        } else {
            rows.insert(UrlSuggestionRecord(url: url, title: title.isEmpty ? url : title, visits: 1, lastVisit: now), at: 0)
        }
        visitRecordsByProfile[profileID] = Array(rows.prefix(200))
        var rawMap: [String: [UrlSuggestionRecord]] = [:]
        for (k, v) in visitRecordsByProfile { rawMap[k.uuidString] = v }
        if let data = try? JSONEncoder().encode(rawMap) {
            UserDefaults.standard.set(data, forKey: Self.visitsKey)
        }
    }

    func urlSuggestions(for input: String) -> [BrowserBookmark] {
        let query = input.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        guard !query.isEmpty else { return [] }
        // Private tabs neither read nor write profile visit ranking (exact UrlSuggestions.kt parity!)
        let isPrivate = activeTab?.isPrivate ?? (activeGroupID == Self.privateGroupID)
        var seen = Set<String>()
        var results: [BrowserBookmark] = []

        if !isPrivate {
            let ranked = (visitRecordsByProfile[activeProfileID] ?? [])
                .filter { $0.url.lowercased().contains(query) || $0.title.lowercased().contains(query) }
                .sorted { a, b in
                    if a.visits != b.visits { return a.visits > b.visits }
                    return a.lastVisit > b.lastVisit
                }
            for row in ranked {
                if seen.insert(row.url.lowercased()).inserted {
                    results.append(BrowserBookmark(title: row.title, url: row.url))
                }
                if results.count >= 6 { return results }
            }
        }

        for bm in bookmarks where bm.title.lowercased().contains(query) || bm.url.lowercased().contains(query) {
            if seen.insert(bm.url.lowercased()).inserted { results.append(bm) }
            if results.count >= 6 { return results }
        }
        if !isPrivate {
            for h in history where h.title.lowercased().contains(query) || h.url.lowercased().contains(query) {
                if seen.insert(h.url.lowercased()).inserted { results.append(BrowserBookmark(title: h.title, url: h.url)) }
                if results.count >= 6 { return results }
            }
        }
        return results
    }

    func openInBackgroundTab(_ rawURL: String) {
        guard let url = URL(string: rawURL) else { return }
        _ = newTab(url: url, afterTabID: activeID, background: true)
        status = "백그라운드 탭을 열었습니다."
    }

    // MARK: - Tab Operations (Including Duplicate Right After Source Tab!)

    @discardableResult
    func newTab(url: URL? = nil, afterTabID: UUID? = nil, background: Bool = false) -> UUID {
        let isPriv = activeGroupID == Self.privateGroupID
        let tab = BrowserTab(
            title: url?.host ?? "새 탭",
            url: url?.absoluteString ?? "about:blank",
            profileID: activeProfileID,
            groupID: activeGroupID,
            isPrivate: isPriv,
            isDesktopMode: preferences.desktopModeDefault,
            pageZoom: preferences.fontScale
        )
        if let anchorID = afterTabID, let anchorIdx = tabs.firstIndex(where: { $0.id == anchorID }) {
            tabs.insert(tab, at: anchorIdx + 1)
        } else {
            tabs.append(tab)
        }
        if !background {
            activeID = tab.id
            isChromeCollapsed = false
            failedLoadURL = nil
            failedLoadMessage = nil
            updateAllWebViewContentInsets()
        }
        if let url {
            loadedTabIDs.insert(tab.id)
            _ = webView(for: tab.id, loadIfNeeded: true)
        }
        persist()
        return tab.id
    }

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
        isChromeCollapsed = false
        failedLoadURL = nil
        failedLoadMessage = nil
        updateAllWebViewContentInsets()
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

    func saveProfile(_ profile: BrowserProfile) {
        if let idx = profiles.firstIndex(where: { $0.id == profile.id }) {
            profiles[idx] = profile
        } else if profiles.count < 8 {
            profiles.append(profile)
            groups.append(BrowserGroup(profileID: profile.id, name: "기본"))
        }
        persist()
    }

    func deleteProfile(_ id: UUID) {
        guard profiles.count > 1, id != Self.defaultProfileID else {
            status = "기본 프로필은 삭제할 수 없습니다."
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

    func renameGroup(_ id: UUID, to newName: String) {
        let trimmed = newName.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty, let idx = groups.firstIndex(where: { $0.id == id }) else { return }
        groups[idx].name = trimmed
        persist()
        status = "탭 그룹 이름을 '\(trimmed)'(으)로 변경했습니다."
    }

    func deleteGroup(_ id: UUID) {
        guard id != Self.privateGroupID else { return }
        let siblings = profileGroups
        guard siblings.count > 1, let targetGroup = siblings.first(where: { $0.id != id }) else {
            status = "기본 탭 그룹은 삭제할 수 없습니다."
            return
        }
        for i in tabs.indices where tabs[i].groupID == id && !tabs[i].isPrivate {
            tabs[i].groupID = targetGroup.id
        }
        groups.removeAll { $0.id == id }
        if activeGroupID == id {
            selectGroup(targetGroup.id)
        } else {
            persist()
        }
        status = "탭 그룹을 삭제하고 탭을 이동했습니다."
    }

    func moveGroup(_ groupID: UUID, toProfile targetProfileID: UUID) {
        guard groupID != Self.privateGroupID,
              profiles.contains(where: { $0.id == targetProfileID }),
              let idx = groups.firstIndex(where: { $0.id == groupID }) else { return }
        let oldProfileID = groups[idx].profileID
        guard oldProfileID != targetProfileID else { return }
        groups[idx].profileID = targetProfileID
        for i in tabs.indices where tabs[i].groupID == groupID && !tabs[i].isPrivate {
            tabs[i].profileID = targetProfileID
        }
        if groups.filter({ $0.profileID == oldProfileID }).isEmpty {
            groups.append(BrowserGroup(profileID: oldProfileID, name: "기본"))
        }
        activeProfileID = targetProfileID
        selectGroup(groupID)
        status = "탭 그룹을 다른 프로필로 이동했습니다."
    }

    func switchWorkspaceGroup(direction: Int) -> Bool {
        let order = workspaceGroupIDs
        guard let currentIdx = order.firstIndex(of: activeGroupID) else { return false }
        let targetIdx = currentIdx + direction
        guard targetIdx >= 0 && targetIdx < order.count else { return false }
        selectGroup(order[targetIdx])
        return true
    }

    func moveTabs(_ tabIDs: Set<UUID>, toProfile profileID: UUID, groupID: UUID) {
        guard !tabIDs.isEmpty else { return }
        let isPriv = groupID == Self.privateGroupID
        for i in tabs.indices where tabIDs.contains(tabs[i].id) {
            tabs[i].profileID = profileID
            tabs[i].groupID = groupID
            tabs[i].isPrivate = isPriv
        }
        if visibleTabs.isEmpty {
            _ = newTab()
        } else if !visibleTabs.contains(where: { $0.id == activeID }) {
            activeID = visibleTabs.first!.id
        }
        persist()
        status = "\(tabIDs.count)개의 탭을 이동했습니다."
    }

    func closeTabs(_ tabIDs: Set<UUID>) {
        guard !tabIDs.isEmpty else { return }
        for id in tabIDs {
            if let tab = tabs.first(where: { $0.id == id }), !tab.isPrivate, tab.url != "about:blank" {
                closedTabs.insert(tab, at: 0)
            }
            webViews.removeValue(forKey: id)?.stopLoading()
            loadedTabIDs.remove(id)
        }
        if closedTabs.count > 25 { closedTabs = Array(closedTabs.prefix(25)) }
        tabs.removeAll { tabIDs.contains($0.id) }
        if visibleTabs.isEmpty {
            _ = newTab()
        } else if tabIDs.contains(activeID) {
            activeID = visibleTabs.last!.id
        }
        persist()
    }

    // MARK: - Page Controls, Find in Page, Zoom, Desktop Mode, Media PiP/Seek

    func back() { webViews[activeID]?.goBack() }
    func forward() { webViews[activeID]?.goForward() }
    func reload() {
        failedLoadURL = nil
        failedLoadMessage = nil
        webViews[activeID]?.reload()
    }
    func stopLoading() {
        webViews[activeID]?.stopLoading()
        isLoading = false
    }
    func retryFailedLoad() {
        let target = failedLoadURL ?? currentURL
        failedLoadURL = nil
        failedLoadMessage = nil
        if let url = URL(string: target), target != "about:blank" {
            webView(for: activeID, loadIfNeeded: false).load(URLRequest(url: url))
        } else {
            reload()
        }
    }
    func home() {
        isChromeCollapsed = false
        failedLoadURL = nil
        failedLoadMessage = nil
        updateAllWebViewContentInsets()
        if preferences.homePage == "about:blank" || preferences.homePage.isEmpty {
            setTab(activeID) { $0.url = "about:blank"; $0.title = "새 탭" }
        } else {
            open(preferences.homePage)
        }
    }

    func findBookmark(for url: String) -> BrowserBookmark? {
        bookmarks.first { $0.url.caseInsensitiveCompare(url) == .orderedSame }
    }

    func saveBookmark(editingID: UUID?, title: String, url: String, folderID: String?) {
        var cleanURL = url.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !cleanURL.isEmpty else { return }
        if !cleanURL.contains("://") { cleanURL = "https://" + cleanURL }
        let cleanTitle = title.trimmingCharacters(in: .whitespacesAndNewlines)
        let resolvedTitle = cleanTitle.isEmpty ? (URL(string: cleanURL)?.host ?? cleanURL) : cleanTitle
        if let editID = editingID, let idx = bookmarks.firstIndex(where: { $0.id == editID }) {
            bookmarks[idx].title = resolvedTitle
            bookmarks[idx].url = cleanURL
            bookmarks[idx].folderID = folderID
            status = "북마크를 수정했습니다."
        } else if let idx = bookmarks.firstIndex(where: { $0.url.caseInsensitiveCompare(cleanURL) == .orderedSame }) {
            bookmarks[idx].title = resolvedTitle
            bookmarks[idx].folderID = folderID
            status = "북마크를 업데이트했습니다."
        } else {
            bookmarks.append(BrowserBookmark(title: resolvedTitle, url: cleanURL, folderID: folderID))
            status = "북마크에 추가했습니다."
        }
        persist()
    }

    func deleteBookmark(_ id: UUID) {
        bookmarks.removeAll { $0.id == id }
        persist()
        status = "북마크를 삭제했습니다."
    }

    func toggleBookmark(folderID: String? = nil) {
        guard let tab = activeTab, tab.url != "about:blank" else { return }
        if let idx = bookmarks.firstIndex(where: { $0.url.caseInsensitiveCompare(tab.url) == .orderedSame }) {
            bookmarks.remove(at: idx)
            status = "북마크에서 제거됨"
        } else {
            bookmarks.append(BrowserBookmark(title: tab.title, url: tab.url, folderID: folderID))
            status = "북마크에 추가됨"
        }
        persist()
    }

    func seekActiveMedia(by seconds: Int) {
        guard let view = webViews[activeID] else { return }
        let js = """
        (() => {
          const media = Array.from(document.querySelectorAll('video, audio')).find(m => !m.paused)
            || document.querySelector('video, audio');
          if (!media) return false;
          const dur = Number.isFinite(media.duration) ? media.duration : 1e9;
          media.currentTime = Math.max(0, Math.min(dur, (media.currentTime || 0) + (\(seconds))));
          return true;
        })();
        """
        view.evaluateJavaScript(js) { [weak self] result, _ in
            Task { @MainActor in
                if (result as? Bool) == true {
                    self?.status = seconds > 0 ? "\(seconds)초 앞으로 이동" : "\(-seconds)초 뒤로 이동"
                } else {
                    self?.status = "현재 페이지에 제어할 미디어가 없습니다."
                }
            }
        }
    }

    func enterActiveVideoPiP() {
        guard let view = webViews[activeID] else { return }
        let js = """
        (() => {
          const v = Array.from(document.querySelectorAll('video')).find(m => !m.paused) || document.querySelector('video');
          if (!v) return false;
          if (typeof v.webkitSetPresentationMode === 'function') {
            v.webkitSetPresentationMode(v.webkitPresentationMode === 'picture-in-picture' ? 'inline' : 'picture-in-picture');
            return true;
          }
          if (document.pictureInPictureEnabled && typeof v.requestPictureInPicture === 'function') {
            v.requestPictureInPicture().catch(() => {});
            return true;
          }
          return false;
        })();
        """
        view.evaluateJavaScript(js) { [weak self] result, _ in
            Task { @MainActor in
                if (result as? Bool) != true {
                    self?.status = "이 페이지에서 PiP 동영상을 찾지 못했습니다."
                }
            }
        }
    }

    func downloadRemoteURL(_ rawURL: String, saveToBookshelfFolder: String? = nil) async {
        guard let url = URL(string: rawURL), let scheme = url.scheme?.lowercased(), (scheme == "http" || scheme == "https") else {
            status = "이 주소는 직접 다운로드할 수 없습니다."
            return
        }
        status = "다운로드 중…"
        var req = URLRequest(url: url)
        if !isStartPage && currentURL.hasPrefix("http") {
            req.setValue(currentURL, forHTTPHeaderField: "Referer")
        }
        do {
            let (data, response) = try await URLSession.shared.data(for: req)
            let mime = (response as? HTTPURLResponse)?.mimeType ?? "application/octet-stream"
            let suggested = response.suggestedFilename ?? (url.lastPathComponent.isEmpty ? "download.bin" : url.lastPathComponent)
            let ext = (suggested as NSString).pathExtension.lowercased()
            let isImg = mime.lowercased().hasPrefix("image/") || ["png", "jpg", "jpeg", "webp", "gif", "heic", "bmp"].contains(ext)
            if let shelfFolder = saveToBookshelfFolder {
                let targetExt = ext.isEmpty ? "png" : ext
                if ShelfModel.shared.addImageData(data, fileExtension: targetExt, toFolder: shelfFolder, sourceUrl: currentURL) != nil {
                    status = "‘\(BookshelfPath.leaf(shelfFolder))’ 책장에 이미지를 저장했습니다."
                } else {
                    status = "책장 이미지 저장에 실패했습니다."
                }
                return
            }
            let temp = FileManager.default.temporaryDirectory.appendingPathComponent("dl-\(UUID().uuidString)-\(suggested)")
            try data.write(to: temp, options: .atomic)
            pendingDownload = PendingDownloadPrompt(
                suggestedFilename: suggested,
                mimeType: mime,
                isImage: isImg,
                tempFileURL: temp
            )
        } catch {
            status = "다운로드 실패: \(error.localizedDescription)"
        }
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
        let clamped = min(2.0, max(0.5, (zoom * 20).rounded() / 20.0))
        setTab(activeID) { $0.pageZoom = clamped }
        preferences.fontScale = clamped
        savePreferences()
        webViews[activeID]?.pageZoom = CGFloat(clamped)
    }

    func findInPage(_ query: String, forward: Bool = true) async {
        let isSameQuery = (query == findQuery && findMatchCount > 0)
        findQuery = query
        guard let view = webViews[activeID] else { return }
        let trimmed = query.trimmingCharacters(in: .whitespacesAndNewlines)
        if trimmed.isEmpty {
            let clearJS = """
            (() => {
              document.querySelectorAll('mark.__omyaru_find_hl').forEach(el => {
                const parent = el.parentNode;
                if (parent) { parent.replaceChild(document.createTextNode(el.textContent || ''), el); parent.normalize(); }
              });
              window.__omyaruFindIdx = -1;
            })();
            """
            _ = try? await view.evaluateJavaScript(clearJS)
            findMatchCount = 0
            findCurrentIndex = 0
            return
        }
        let escaped = (try? JSONEncoder().encode(trimmed)).flatMap { String(data: $0, encoding: .utf8) } ?? "\"\""
        let stepDelta = isSameQuery ? (forward ? 1 : -1) : 0
        let js = """
        (() => {
          const q = \(escaped);
          const step = \(stepDelta);
          const rebuild = (step === 0 || window.__omyaruFindQuery !== q);
          if (rebuild) {
            document.querySelectorAll('mark.__omyaru_find_hl').forEach(el => {
              const p = el.parentNode;
              if (p) { p.replaceChild(document.createTextNode(el.textContent || ''), el); p.normalize(); }
            });
            window.__omyaruFindQuery = q;
            window.__omyaruFindIdx = 0;
            const lowerQ = q.toLowerCase();
            const walker = document.createTreeWalker(document.body || document.documentElement, NodeFilter.SHOW_TEXT, {
              acceptNode(node) {
                if (!node.nodeValue || !node.nodeValue.trim()) return NodeFilter.FILTER_REJECT;
                const tag = node.parentElement ? node.parentElement.tagName : '';
                if (tag === 'SCRIPT' || tag === 'STYLE' || tag === 'NOSCRIPT' || tag === 'TEXTAREA') return NodeFilter.FILTER_REJECT;
                return node.nodeValue.toLowerCase().includes(lowerQ) ? NodeFilter.FILTER_ACCEPT : NodeFilter.FILTER_SKIP;
              }
            });
            const nodes = [];
            while (walker.nextNode() && nodes.length < 400) nodes.push(walker.currentNode);
            for (const textNode of nodes) {
              const text = textNode.nodeValue || '';
              const lower = text.toLowerCase();
              let pos = 0, idx = lower.indexOf(lowerQ, pos);
              if (idx < 0) continue;
              const frag = document.createDocumentFragment();
              while (idx >= 0) {
                if (idx > pos) frag.appendChild(document.createTextNode(text.slice(pos, idx)));
                const mark = document.createElement('mark');
                mark.className = '__omyaru_find_hl';
                mark.style.cssText = 'background:#FFE066;color:#111;border-radius:2px;padding:0 1px;';
                mark.textContent = text.slice(idx, idx + q.length);
                frag.appendChild(mark);
                pos = idx + q.length;
                idx = lower.indexOf(lowerQ, pos);
              }
              if (pos < text.length) frag.appendChild(document.createTextNode(text.slice(pos)));
              textNode.parentNode?.replaceChild(frag, textNode);
            }
          }
          const marks = Array.from(document.querySelectorAll('mark.__omyaru_find_hl'));
          if (!marks.length) return [0, 0];
          let cur = typeof window.__omyaruFindIdx === 'number' ? window.__omyaruFindIdx : 0;
          if (!rebuild) cur = (cur + step + marks.length) % marks.length;
          window.__omyaruFindIdx = cur;
          marks.forEach((m, i) => {
            m.style.background = i === cur ? '#FF8A2B' : '#FFE066';
            m.style.color = '#111';
          });
          marks[cur].scrollIntoView({ behavior: 'smooth', block: 'center' });
          return [cur + 1, marks.length];
        })();
        """
        if let pair = try? await view.evaluateJavaScript(js) as? [Int], pair.count == 2 {
            findCurrentIndex = pair[0]
            findMatchCount = pair[1]
        }
    }

    func clearBrowsingData() async {
        let types = WKWebsiteDataStore.allWebsiteDataTypes()
        await WKWebsiteDataStore.default().removeData(ofTypes: types, modifiedSince: .distantPast)
        history.removeAll()
        clearTabPreviews()
        persist()
        status = "모든 방문 기록·쿠키·캐시·미리보기를 삭제했습니다."
    }

    // MARK: - Downloads & Confirmation Dialog (Android DownloadConfirmationDialog Parity!)

    func commitPendingDownload(fileName: String, saveToBookshelfFolder: String?) {
        guard let pending = pendingDownload else { return }
        defer {
            try? FileManager.default.removeItem(at: pending.tempFileURL)
            pendingDownload = nil
        }
        let safeName = URL(fileURLWithPath: fileName.trimmingCharacters(in: .whitespacesAndNewlines)).lastPathComponent
        let resolvedName = safeName.isEmpty ? pending.suggestedFilename : safeName
        if let shelfFolder = saveToBookshelfFolder,
           let data = try? Data(contentsOf: pending.tempFileURL) {
            let ext = (resolvedName as NSString).pathExtension
            _ = ShelfModel.shared.addImageData(data, fileExtension: ext.isEmpty ? "png" : ext, toFolder: shelfFolder, sourceUrl: currentURL)
            status = "‘\(BookshelfPath.leaf(shelfFolder))’ 책장에 이미지를 저장했습니다."
            return
        }
        let directory = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("Downloads", isDirectory: true)
        do {
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
            var destination = directory.appendingPathComponent(resolvedName)
            if FileManager.default.fileExists(atPath: destination.path) {
                destination = directory.appendingPathComponent("\(Int(Date().timeIntervalSince1970))-" + resolvedName)
            }
            try FileManager.default.copyItem(at: pending.tempFileURL, to: destination)
            refreshDownloadsList()
            status = "다운로드 저장 완료: \(destination.lastPathComponent)"
        } catch {
            status = "다운로드 저장 실패: \(error.localizedDescription)"
        }
    }

    func cancelPendingDownload() {
        if let pending = pendingDownload {
            try? FileManager.default.removeItem(at: pending.tempFileURL)
        }
        pendingDownload = nil
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
            failedLoadURL = nil
            failedLoadMessage = nil
            if isChromeCollapsed {
                isChromeCollapsed = false
                updateAllWebViewContentInsets()
            }
        }
    }

    func webView(_ webView: WKWebView, didCommit navigation: WKNavigation!) {
        if id(for: webView) == activeID {
            estimatedProgress = max(0.45, webView.estimatedProgress)
            failedLoadURL = nil
            failedLoadMessage = nil
        }
    }

    func webView(_ webView: WKWebView, didFinish navigation: WKNavigation!) {
        guard let id = id(for: webView) else { return }
        if id == activeID {
            isLoading = false
            estimatedProgress = 1.0
            failedLoadURL = nil
            failedLoadMessage = nil
        }
        let url = webView.url?.absoluteString ?? "about:blank"
        let title = (webView.title?.isEmpty == false) ? webView.title! : url
        setTab(id) { tab in
            tab.url = url
            tab.title = title
        }
        if let tab = tabs.first(where: { $0.id == id }), !tab.isPrivate && url != "about:blank" {
            recordVisitSuggestion(profileID: tab.profileID, url: url, title: title)
            history.removeAll { $0.url == url }
            history.insert(HistoryEntry(title: title, url: url), at: 0)
            persist()
        }
        captureSnapshot(for: id)
        objectWillChange.send()
    }

    func webView(_ webView: WKWebView, didFailProvisionalNavigation navigation: WKNavigation!, withError error: Error) {
        if id(for: webView) == activeID { isLoading = false }
        let nsErr = error as NSError
        if nsErr.code == NSURLErrorCancelled { return }
        if id(for: webView) == activeID {
            let failing = (nsErr.userInfo[NSURLErrorFailingURLStringErrorKey] as? String) ?? webView.url?.absoluteString ?? currentURL
            failedLoadURL = failing
            failedLoadMessage = error.localizedDescription
        }
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
        let host = navigationAction.request.url?.host?.lowercased() ?? ""
        if preferences.trackingProtection != .off,
           !preferences.contentBlockingExceptions.contains(currentHost),
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
        let safe = URL(fileURLWithPath: suggestedFilename).lastPathComponent
        let temp = FileManager.default.temporaryDirectory.appendingPathComponent("dl-\(UUID().uuidString)-\(safe)")
        activeDownloadTempInfo = (temp, safe, response.mimeType ?? "application/octet-stream")
        completionHandler(temp)
    }

    func downloadDidFinish(_ download: WKDownload) {
        guard let info = activeDownloadTempInfo else { return }
        activeDownloadTempInfo = nil
        let ext = (info.suggestedName as NSString).pathExtension.lowercased()
        let isImg = info.mimeType.lowercased().hasPrefix("image/") || ["png", "jpg", "jpeg", "webp", "gif", "heic", "bmp"].contains(ext)
        pendingDownload = PendingDownloadPrompt(
            suggestedFilename: info.suggestedName,
            mimeType: info.mimeType,
            isImage: isImg,
            tempFileURL: info.url
        )
    }

    func refreshDownloadsList() {
        let directory = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("Downloads", isDirectory: true)
        downloads = (try? FileManager.default.contentsOfDirectory(at: directory, includingPropertiesForKeys: nil)) ?? []
    }
}
