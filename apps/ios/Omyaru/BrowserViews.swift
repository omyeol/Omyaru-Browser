import SwiftUI
import WebKit
import UIKit

struct BrowserWebView: UIViewRepresentable {
    let webView: WKWebView
    func makeUIView(context: Context) -> WKWebView { webView }
    func updateUIView(_ view: WKWebView, context: Context) {}
}

// MARK: - Main Browser Screen

struct BrowserScreen: View {
    @EnvironmentObject private var model: BrowserModel
    @State private var address: String = ""
    @State private var showTabs = false
    @State private var showBookmarks = false
    @State private var showBookmarkEdit = false
    @State private var showHistory = false
    @State private var showDownloads = false
    @State private var showBookshelf = false
    @State private var showCaptureMenu = false
    @State private var showLiveCapture = false
    @State private var showWatermarkRemover = false
    @State private var pendingWatermarkImage: UIImage? = nil
    @State private var showDriveSync = false
    @State private var showExtensions = false
    @State private var showNetworkPrivacy = false
    @State private var showAppLock = false
    @State private var showSettings = false
    @State private var showProfiles = false
    @State private var showPageTools = false
    @State private var showBrowserMenu = false
    @State private var showToolbarEditor = false
    @State private var showCloseAllConfirm = false
    @State private var sharePayload: [Any]? = nil
    @State private var showFindBar = false
    @State private var findInput = ""
    @State private var showAddFavorite = false
    @State private var newFavTitle = ""
    @State private var newFavURL = ""
    @State private var updateMessage: String? = nil
    @FocusState private var addressFocused: Bool

    var body: some View {
        let isBottomBar = model.preferences.addressBarPosition == .bottom
        ZStack {
            Color(red: 0.08, green: 0.08, blue: 0.09).ignoresSafeArea()

            // Main Browser Surface + Floating Toolbar (Smoothly Hero-Zooms & Blurs behind Fullscreen TabOverview!)
            ZStack {
                if let tab = model.activeTab {
                    if model.isStartPage {
                        startPageView
                    } else {
                        ZStack {
                            BrowserWebView(webView: model.webView(for: tab.id, loadIfNeeded: true))
                                .id(tab.id)
                                .ignoresSafeArea(edges: .bottom)

                            FastScrollBarOverlay(model: model)
                                .padding(.top, isBottomBar ? 8 : (model.isChromeCollapsed ? 58 : 124))
                                .padding(.bottom, isBottomBar ? (model.isChromeCollapsed ? 58 : 124) : 8)

                            if let failedURL = model.failedLoadURL {
                                pageErrorOverlay(failedURL: failedURL)
                            }
                        }
                    }
                }

                if showFindBar && !model.isStartPage {
                    VStack {
                        if !isBottomBar {
                            Spacer().frame(height: model.isChromeCollapsed ? 56 : 122)
                        }
                        findInPageBar
                        Spacer()
                    }
                }

                // Floating Status Toast + Floating Liquid Glass URL Island (Top or Bottom!)
                VStack(spacing: 6) {
                    if isBottomBar {
                        Spacer(minLength: 0)
                    }

                    if isBottomBar && !model.status.isEmpty {
                        statusToastPill
                    }

                    floatingAndroidToolbarRegion

                    if !isBottomBar && !model.status.isEmpty {
                        statusToastPill
                    }

                    if !isBottomBar {
                        Spacer(minLength: 0)
                    }
                }
                .animation(.spring(response: 0.30, dampingFraction: 0.84), value: model.isChromeCollapsed)
                .animation(.easeInOut(duration: 0.2), value: model.status)
            }
            .clipShape(RoundedRectangle(cornerRadius: showTabs ? 28 : 0, style: .continuous))
            .scaleEffect(showTabs ? 0.88 : 1.0)
            .blur(radius: showTabs ? 18 : 0)
            .opacity(showTabs ? 0.52 : 1.0)
            .allowsHitTesting(!showTabs && !showPageTools && !showBrowserMenu)
            .animation(.spring(response: 0.36, dampingFraction: 0.86), value: showTabs)

            // Exact Video 00:06->00:08 Morphing Liquid Glass Page Tools Popover
            LiquidGlassMorphContainer(
                isPresented: showPageTools,
                alignment: isBottomBar ? .bottomLeading : .topLeading,
                anchor: isBottomBar ? .bottomLeading : .topLeading,
                edgeInsets: isBottomBar
                    ? EdgeInsets(top: 0, leading: 14, bottom: model.isChromeCollapsed ? 62 : 126, trailing: 14)
                    : EdgeInsets(top: model.isChromeCollapsed ? 62 : 126, leading: 14, bottom: 0, trailing: 14),
                onDismiss: { showPageTools = false }
            ) {
                LiquidGlassPageToolsPopover(
                    model: model,
                    onDismiss: {
                        withAnimation(.spring(response: 0.30, dampingFraction: 0.84)) { showPageTools = false }
                    },
                    onBookmarkEdit: { showBookmarkEdit = true },
                    onFindInPage: { showFindBar = true },
                    onOpenCapture: { showLiveCapture = true },
                    onOpenWatermark: { showWatermarkRemover = true },
                    onOpenExtensions: { showExtensions = true }
                )
            }

            // Morphing Liquid Glass Browser Menu Popover (Bottom-Right / Top-Right)
            LiquidGlassMorphContainer(
                isPresented: showBrowserMenu,
                alignment: isBottomBar ? .bottomTrailing : .topTrailing,
                anchor: isBottomBar ? .bottomTrailing : .topTrailing,
                edgeInsets: isBottomBar
                    ? EdgeInsets(top: 0, leading: 14, bottom: model.isChromeCollapsed ? 62 : 126, trailing: 14)
                    : EdgeInsets(top: model.isChromeCollapsed ? 62 : 126, leading: 14, bottom: 0, trailing: 14),
                onDismiss: { showBrowserMenu = false }
            ) {
                LiquidGlassBrowserMenuPopover(
                    model: model,
                    onDismiss: {
                        withAnimation(.spring(response: 0.30, dampingFraction: 0.84)) { showBrowserMenu = false }
                    },
                    onAction: { action in triggerToolbarShortcut(action) },
                    onOpenCaptureMenu: { showCaptureMenu = true },
                    onOpenDownloads: { showDownloads = true },
                    onOpenDriveSync: { showDriveSync = true },
                    onOpenToolbarEditor: { showToolbarEditor = true },
                    onCheckUpdates: { Task { await checkUpdates() } }
                )
            }

            // Exact Video 00:00->00:01->00:04 Fullscreen Hero-Zoom Liquid Glass Tab Overview!
            if showTabs {
                TabOverview(onDismiss: {
                    withAnimation(.spring(response: 0.36, dampingFraction: 0.86)) {
                        showTabs = false
                    }
                })
                .environmentObject(model)
                .transition(.asymmetric(
                    insertion: .opacity.combined(with: .scale(scale: 1.08)),
                    removal: .opacity.combined(with: .scale(scale: 1.06))
                ))
                .zIndex(20)
            }
        }
        .animation(.spring(response: 0.36, dampingFraction: 0.86), value: showTabs)
        .sheet(isPresented: $showBookmarks) {
            BookmarksSheet().environmentObject(model)
        }
        .sheet(isPresented: $showBookmarkEdit) {
            BookmarkEditSheet(
                model: model,
                existing: model.findBookmark(for: model.currentURL),
                defaultTitle: model.activeTab?.title ?? model.currentHost,
                defaultURL: model.currentURL
            )
        }
        .sheet(isPresented: $showHistory) {
            HistorySheet().environmentObject(model)
        }
        .sheet(isPresented: $showDownloads) {
            DownloadsSheet().environmentObject(model)
        }
        .sheet(isPresented: $showBookshelf) {
            BookshelfView()
        }
        .sheet(isPresented: $showCaptureMenu) {
            CaptureActionMenuSheet(
                onOpenLiveCapture: { showLiveCapture = true },
                onOpenWatermark: { showWatermarkRemover = true },
                onOpenBookshelf: { showBookshelf = true }
            )
        }
        .sheet(isPresented: $showLiveCapture) {
            LiveCaptureView(
                webView: model.activeWebView(),
                sourceURL: model.currentURL,
                sideMargins: model.preferences.captureSideMargins,
                onSendToWatermark: { cropped in
                    pendingWatermarkImage = cropped
                    DispatchQueue.main.asyncAfter(deadline: .now() + 0.25) {
                        showWatermarkRemover = true
                    }
                }
            )
        }
        .sheet(isPresented: $showWatermarkRemover, onDismiss: {
            pendingWatermarkImage = nil
        }) {
            WatermarkRemoverView(
                webView: model.activeWebView(),
                sourceURL: model.currentURL,
                initialCapturedImage: pendingWatermarkImage,
                configuredWebToolURL: model.preferences.watermarkRemoverUrl
            )
        }
        .sheet(item: $model.pendingDownload) { prompt in
            DownloadConfirmationSheet(prompt: prompt, model: model)
        }
        .sheet(item: $model.activeContextElement) { element in
            WebContextElementSheet(model: model, element: element) { urlStr in
                if let url = URL(string: urlStr) {
                    sharePayload = [url]
                } else {
                    sharePayload = [urlStr]
                }
            }
        }
        .sheet(isPresented: Binding(get: { sharePayload != nil }, set: { if !$0 { sharePayload = nil } })) {
            if let items = sharePayload {
                ActivityShareSheet(items: items)
            }
        }
        .sheet(isPresented: $showToolbarEditor) {
            ToolbarEditorSheet(model: model)
        }
        .sheet(isPresented: $showCloseAllConfirm) {
            CloseAllTabsCountdownSheet(tabCount: model.visibleTabs.count) {
                model.closeAllVisibleTabs()
            }
        }
        .sheet(isPresented: $showDriveSync) {
            DriveSyncSheet(browser: model, shelf: .shared)
        }
        .sheet(isPresented: $showExtensions) {
            ExtensionSettingsSheet(model: model)
        }
        .sheet(isPresented: $showNetworkPrivacy) {
            NetworkPrivacySheet(model: model)
        }
        .sheet(isPresented: $showAppLock) {
            AppLockSettingsSheet()
        }
        .sheet(isPresented: $showSettings) {
            SettingsSheet(
                model: model,
                onOpenSync: { showSettings = false; DispatchQueue.main.asyncAfter(deadline: .now() + 0.25) { showDriveSync = true } },
                onOpenExtensions: { showSettings = false; DispatchQueue.main.asyncAfter(deadline: .now() + 0.25) { showExtensions = true } },
                onOpenNetwork: { showSettings = false; DispatchQueue.main.asyncAfter(deadline: .now() + 0.25) { showNetworkPrivacy = true } },
                onOpenAppLock: { showSettings = false; DispatchQueue.main.asyncAfter(deadline: .now() + 0.25) { showAppLock = true } },
                onOpenProfiles: { showSettings = false; DispatchQueue.main.asyncAfter(deadline: .now() + 0.25) { showProfiles = true } },
                onCheckUpdates: { Task { await checkUpdates() } }
            )
        }
        .sheet(isPresented: $showProfiles) {
            ProfileManagerSheet().environmentObject(model)
        }
        .alert("즐겨찾기 추가", isPresented: $showAddFavorite) {
            TextField("이름 (예: 네이버)", text: $newFavTitle)
            TextField("주소 (예: https://naver.com)", text: $newFavURL)
                .textInputAutocapitalization(.never)
                .autocorrectionDisabled()
                .keyboardType(.URL)
            Button("추가") {
                let t = newFavTitle.trimmingCharacters(in: .whitespacesAndNewlines)
                var u = newFavURL.trimmingCharacters(in: .whitespacesAndNewlines)
                guard !u.isEmpty else { return }
                if !u.contains("://") { u = "https://" + u }
                model.bookmarks.append(BrowserBookmark(title: t.isEmpty ? u : t, url: u))
                model.persist()
                newFavTitle = ""
                newFavURL = ""
            }
            Button("취소", role: .cancel) {
                newFavTitle = ""
                newFavURL = ""
            }
        }
        .alert("업데이트 확인", isPresented: Binding(get: { updateMessage != nil }, set: { if !$0 { updateMessage = nil } })) {
            Button("확인", role: .cancel) { updateMessage = nil }
        } message: {
            Text(updateMessage ?? "")
        }
        .onChange(of: model.activeID) { _ in
            syncAddressField()
            if !model.preferences.keepKeyboardAcrossTabSwitch {
                if model.isStartPage && model.preferences.showKeyboardOnNewTab {
                    addressFocused = true
                } else {
                    addressFocused = false
                }
            }
        }
        .onChange(of: model.currentURL) { _ in
            if !addressFocused { syncAddressField() }
        }
        .onChange(of: addressFocused) { focused in
            if focused {
                if model.isChromeCollapsed {
                    model.isChromeCollapsed = false
                    model.updateAllWebViewContentInsets()
                }
                address = model.currentURL == "about:blank" ? "" : model.currentURL
            } else {
                syncAddressField()
            }
        }
        .onAppear {
            syncAddressField()
        }
    }

    private func syncAddressField() {
        let raw = model.currentURL
        if raw == "about:blank" || raw.isEmpty {
            address = ""
        } else if model.preferences.showFullURL || addressFocused {
            address = raw
        } else {
            let host = URL(string: raw)?.host?.replacingOccurrences(of: "^www\\.", with: "", options: .regularExpression)
            address = host ?? raw
        }
    }

    private var statusToastPill: some View {
        Text(model.status)
            .font(.caption.weight(.medium))
            .foregroundStyle(.white)
            .lineLimit(2)
            .multilineTextAlignment(.center)
            .padding(.horizontal, 14)
            .padding(.vertical, 8)
            .background(Color.black.opacity(0.78), in: Capsule())
            .overlay(Capsule().strokeBorder(Color.white.opacity(0.16), lineWidth: 0.8))
            .shadow(color: .black.opacity(0.35), radius: 8, y: 3)
            .padding(.horizontal, 20)
            .transition(.opacity.combined(with: .move(edge: .bottom)))
    }

    private func pageErrorOverlay(failedURL: String) -> some View {
        let host = URL(string: failedURL)?.host ?? failedURL
        return VStack(spacing: 14) {
            Image(systemName: "wifi.exclamationmark")
                .font(.system(size: 44))
                .foregroundStyle(Color(red: 0.545, green: 0.671, blue: 1.0))
            Text("페이지를 불러올 수 없습니다")
                .font(.title3.weight(.bold))
                .foregroundStyle(.primary)
            Text(host)
                .font(.subheadline)
                .foregroundStyle(.secondary)
                .lineLimit(1)
            Text(model.failedLoadMessage ?? "네트워크 연결 또는 사이트 주소를 확인한 뒤 다시 시도해 주세요.")
                .font(.footnote)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
                .padding(.horizontal, 18)
            Button {
                model.retryFailedLoad()
            } label: {
                Label("다시 시도", systemImage: "arrow.clockwise")
                    .font(.subheadline.weight(.semibold))
                    .padding(.horizontal, 22)
                    .padding(.vertical, 10)
            }
            .buttonStyle(.borderedProminent)
            .padding(.top, 6)
        }
        .padding(24)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(Color(red: 0.09, green: 0.075, blue: 0.067))
    }

    // MARK: - Safari-Style Start Page

    private var startPageView: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 22) {
                HStack {
                    Button {
                        showProfiles = true
                    } label: {
                        HStack(spacing: 6) {
                            Image(systemName: model.activeProfile.icon)
                                .foregroundStyle(Color(hex: model.activeProfile.colorHex))
                            Text(model.activeGroupID == BrowserModel.privateGroupID ? "비공개 브라우징" : model.activeProfile.name)
                                .font(.subheadline.weight(.semibold))
                            Image(systemName: "chevron.down")
                                .font(.caption2)
                        }
                        .padding(.horizontal, 12)
                        .padding(.vertical, 7)
                        .background(chromeSurfaceColor, in: Capsule())
                    }
                    .buttonStyle(.plain)

                    Spacer()

                    Button { showNetworkPrivacy = true } label: {
                        Image(systemName: "network.badge.shield.half.filled")
                            .padding(8)
                            .background(chromeSurfaceColor, in: Circle())
                    }
                    .buttonStyle(.plain)

                    Button { showDriveSync = true } label: {
                        Image(systemName: "arrow.triangle.2.circlepath.icloud")
                            .padding(8)
                            .background(chromeSurfaceColor, in: Circle())
                    }
                    .buttonStyle(.plain)

                    Button { showSettings = true } label: {
                        Image(systemName: "gearshape")
                            .padding(8)
                            .background(chromeSurfaceColor, in: Circle())
                    }
                    .buttonStyle(.plain)
                }

                // 1. Favorites Section
                VStack(alignment: .leading, spacing: 12) {
                    Text("즐겨찾기").font(.headline.weight(.bold))
                    LazyVGrid(columns: Array(repeating: GridItem(.flexible(), spacing: 12), count: 4), spacing: 14) {
                        ForEach(model.bookmarks.prefix(11)) { bm in
                            Button {
                                model.open(bm.url)
                            } label: {
                                VStack(spacing: 6) {
                                    ZStack {
                                        RoundedRectangle(cornerRadius: 16, style: .continuous)
                                            .fill(chromeCardColor)
                                            .frame(width: 58, height: 58)
                                        Circle()
                                            .fill(tileColor(for: bm.title))
                                            .frame(width: 32, height: 32)
                                            .overlay {
                                                Text(String(bm.title.prefix(1)).uppercased())
                                                    .font(.subheadline.weight(.bold))
                                                    .foregroundStyle(.white)
                                            }
                                    }
                                    Text(bm.title)
                                        .font(.caption)
                                        .foregroundStyle(.primary)
                                        .lineLimit(1)
                                }
                            }
                            .buttonStyle(.plain)
                            .contextMenu {
                                Button(role: .destructive) {
                                    model.bookmarks.removeAll { $0.id == bm.id }
                                    model.persist()
                                } label: {
                                    Label("즐겨찾기 삭제", systemImage: "trash")
                                }
                            }
                        }

                        Button {
                            newFavTitle = ""
                            newFavURL = ""
                            showAddFavorite = true
                        } label: {
                            VStack(spacing: 6) {
                                RoundedRectangle(cornerRadius: 16, style: .continuous)
                                    .fill(chromeCardColor)
                                    .frame(width: 58, height: 58)
                                    .overlay {
                                        Image(systemName: "plus")
                                            .font(.title3.weight(.semibold))
                                            .foregroundStyle(.blue)
                                    }
                                Text("추가").font(.caption).foregroundStyle(.secondary)
                            }
                        }
                        .buttonStyle(.plain)
                    }
                }

                // 2. Privacy Report Card
                VStack(alignment: .leading, spacing: 10) {
                    Text("개인정보 보호 리포트").font(.headline.weight(.bold))
                    HStack(spacing: 12) {
                        ZStack {
                            RoundedRectangle(cornerRadius: 12)
                                .fill(Color.blue.opacity(0.16))
                                .frame(width: 42, height: 42)
                            Image(systemName: "shield.lefthalf.filled")
                                .font(.title3)
                                .foregroundStyle(.blue)
                        }
                        VStack(alignment: .leading, spacing: 3) {
                            Text("추적 방지(\(model.preferences.trackingProtection.rawValue)) · GPC 활성화됨")
                                .font(.system(size: 12.5, weight: .semibold))
                            Text("차단 \(model.blockedTrackerCount)건 · \(model.tabs.count)개 탭 · \(model.profiles.count)개 프로필 로컬 보호 중")
                                .font(.caption2)
                                .foregroundStyle(.secondary)
                        }
                        Spacer()
                    }
                    HStack(spacing: 8) {
                        Button {
                            model.restoreClosedTab()
                        } label: {
                            Label("닫은 탭 복원", systemImage: "arrow.uturn.backward")
                                .font(.caption.weight(.semibold))
                                .padding(.horizontal, 12)
                                .padding(.vertical, 8)
                                .background(chromeSurfaceColor, in: Capsule())
                        }
                        .buttonStyle(.plain)

                        Button {
                            showBookshelf = true
                        } label: {
                            Label("책장 열기", systemImage: "books.vertical.fill")
                                .font(.caption.weight(.semibold))
                                .foregroundStyle(.white)
                                .padding(.horizontal, 14)
                                .padding(.vertical, 8)
                                .background(Color.blue, in: Capsule())
                        }
                        .buttonStyle(.plain)
                    }
                }
                .padding(16)
                .background(chromeCardColor, in: RoundedRectangle(cornerRadius: 18))

                // 3. Quick Tools & Library
                VStack(alignment: .leading, spacing: 10) {
                    Text("빠른 도구 및 라이브러리").font(.headline.weight(.bold))
                    LazyVGrid(columns: [GridItem(.flexible()), GridItem(.flexible())], spacing: 10) {
                        quickToolCard(icon: "books.vertical.fill", title: "책장 라이브러리", subtitle: "폴더 트리·뷰어·PDF 내보내기") { showBookshelf = true }
                        quickToolCard(icon: "viewfinder", title: "실시간 영역 캡처", subtitle: "아래로 확장 스티칭·책장 저장") { showLiveCapture = true }
                        quickToolCard(icon: "wand.and.stars", title: "워터마크 제거", subtitle: "내장 복원 · 웹 도구 ⚡ 사진 주입") { showWatermarkRemover = true }
                        quickToolCard(icon: "arrow.triangle.2.circlepath.icloud.fill", title: "Google Drive 동기화", subtitle: "Android · PC · iOS 암호화 연동") { showDriveSync = true }
                        quickToolCard(icon: "puzzlepiece.extension.fill", title: "확장 기능 · 유저스크립트", subtitle: "이미지 선로딩 · .user.js 관리") { showExtensions = true }
                        quickToolCard(icon: "network.badge.shield.half.filled", title: "VPN · 보안 DNS", subtitle: "WireGuard .conf · 1.1.1.1 DoH") { showNetworkPrivacy = true }
                    }
                }
            }
            .padding(18)
            .padding(.top, model.preferences.addressBarPosition == .top ? 124 : 0)
            .padding(.bottom, model.preferences.addressBarPosition == .bottom ? 132 : 20)
        }
    }

    private var chromeSurfaceColor: Color {
        model.preferences.liquidGlassUi ? Color.white.opacity(0.14) : Color(red: 0.17, green: 0.17, blue: 0.20)
    }

    private var chromeCardColor: Color {
        model.preferences.liquidGlassUi ? Color.white.opacity(0.09) : Color(red: 0.13, green: 0.13, blue: 0.15)
    }

    private func quickToolCard(icon: String, title: String, subtitle: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            HStack(spacing: 10) {
                Image(systemName: icon).font(.title3).foregroundStyle(.blue).frame(width: 28)
                VStack(alignment: .leading, spacing: 2) {
                    Text(title).font(.caption.weight(.bold)).foregroundStyle(.primary).lineLimit(1)
                    Text(subtitle).font(.caption2).foregroundStyle(.secondary).lineLimit(2)
                }
                Spacer(minLength: 0)
            }
            .padding(12)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(chromeCardColor, in: RoundedRectangle(cornerRadius: 14))
        }
        .buttonStyle(.plain)
    }

    private func tileColor(for title: String) -> Color {
        let palette: [Color] = [.blue, .green, .indigo, .orange, .pink, .purple, .teal]
        return palette[abs(title.hashValue) % palette.count]
    }

    // MARK: - Find in Page Bar

    private var findInPageBar: some View {
        HStack(spacing: 8) {
            Image(systemName: "magnifyingglass").foregroundStyle(.secondary)
            TextField("페이지 내 검색...", text: $findInput)
                .textInputAutocapitalization(.never)
                .autocorrectionDisabled()
                .submitLabel(.search)
                .onSubmit { Task { await model.findInPage(findInput, forward: true) } }
                .onChange(of: findInput) { val in Task { await model.findInPage(val, forward: true) } }
            if model.findMatchCount > 0 {
                Text("\(model.findCurrentIndex)/\(model.findMatchCount)")
                    .font(.caption.monospacedDigit())
                    .foregroundStyle(.secondary)
            }
            Button { Task { await model.findInPage(findInput, forward: false) } } label: { Image(systemName: "chevron.up") }
            Button { Task { await model.findInPage(findInput, forward: true) } } label: { Image(systemName: "chevron.down") }
            Button("완료") {
                showFindBar = false
                findInput = ""
                Task { await model.findInPage("", forward: true) }
            }
            .font(.subheadline.weight(.semibold))
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 9)
        .background(.ultraThinMaterial, in: RoundedRectangle(cornerRadius: 14))
        .padding(.horizontal, 12)
        .padding(.top, 6)
    }

    // MARK: - Android-Exact Floating Bottom/Top URL Island + Compact Collapsed Pill + Live Suggestions

    private var androidAccentColor: Color { Color(red: 0.545, green: 0.671, blue: 1.0) }
    private var androidFieldColor: Color { Color(red: 0.149, green: 0.129, blue: 0.122) }
    private var androidOutlineColor: Color { Color(red: 0.286, green: 0.251, blue: 0.235) }
    private var androidOnSurfaceColor: Color { Color(red: 0.945, green: 0.910, blue: 0.894) }
    private var androidMutedColor: Color { Color(red: 0.725, green: 0.682, blue: 0.667) }

    private var floatingAndroidToolbarRegion: some View {
        let isBottom = model.preferences.addressBarPosition == .bottom
        let suggestions = (addressFocused && !address.isEmpty) ? model.urlSuggestions(for: address) : []
        return VStack(spacing: 4) {
            if isBottom && !suggestions.isEmpty {
                floatingSuggestionsCard(suggestions)
            }
            if isBottom && model.isLoading {
                androidProgressStrip
            }

            if model.isChromeCollapsed && !model.isStartPage {
                androidCollapsedPill
                    .transition(.scale(scale: 0.88).combined(with: .opacity))
            } else {
                androidExpandedIsland
                    .transition(.opacity.combined(with: .scale(scale: 0.97)))
            }

            if !isBottom && model.isLoading {
                androidProgressStrip
            }
            if !isBottom && !suggestions.isEmpty {
                floatingSuggestionsCard(suggestions)
            }
        }
        .padding(.horizontal, 8)
        .padding(.top, isBottom ? 0 : 6)
        .padding(.bottom, isBottom ? 6 : 0)
    }

    private var androidProgressStrip: some View {
        GeometryReader { geo in
            ZStack(alignment: .leading) {
                Capsule().fill(Color.white.opacity(0.08))
                Capsule()
                    .fill(androidAccentColor)
                    .frame(width: max(18, geo.size.width * CGFloat(max(0.08, model.estimatedProgress))))
            }
        }
        .frame(height: 2.5)
        .padding(.horizontal, 14)
    }

    private func floatingSuggestionsCard(_ suggestions: [BrowserBookmark]) -> some View {
        VStack(spacing: 0) {
            ForEach(Array(suggestions.enumerated()), id: \.element.id) { idx, item in
                Button {
                    address = item.url
                    model.open(item.url)
                    addressFocused = false
                } label: {
                    HStack(spacing: 10) {
                        Image(systemName: "globe")
                            .font(.caption)
                            .foregroundStyle(androidAccentColor)
                            .frame(width: 20)
                        VStack(alignment: .leading, spacing: 1) {
                            Text(item.title)
                                .font(.subheadline.weight(.medium))
                                .foregroundStyle(androidOnSurfaceColor)
                                .lineLimit(1)
                            Text(item.url)
                                .font(.caption2)
                                .foregroundStyle(androidMutedColor)
                                .lineLimit(1)
                        }
                        Spacer(minLength: 4)
                        Image(systemName: "arrow.up.left")
                            .font(.caption2)
                            .foregroundStyle(androidMutedColor)
                    }
                    .padding(.horizontal, 14)
                    .padding(.vertical, 10)
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                if idx < suggestions.count - 1 {
                    Divider().background(androidOutlineColor.opacity(0.6))
                }
            }
        }
        .background(
            LiquidGlassShapeBackground(
                shape: RoundedRectangle(cornerRadius: 22, style: .continuous),
                tintOpacity: 0.56,
                rimOpacity: 0.36,
                specularOpacity: 0.20,
                shadowRadius: 16
            )
        )
        .clipShape(RoundedRectangle(cornerRadius: 22, style: .continuous))
        .padding(.horizontal, 4)
    }

    // Exact Android + iOS 26 Safari Liquid Glass Collapsed Floating Pill
    private var androidCollapsedPill: some View {
        HStack(spacing: 6) {
            Button {
                withAnimation(.spring(response: 0.34, dampingFraction: 0.80)) {
                    showPageTools.toggle()
                }
            } label: {
                Image(systemName: "line.3.horizontal.decrease")
                    .font(.system(size: 13, weight: .semibold))
                    .foregroundStyle(model.isContentBlockingDisabledForCurrentHost ? .orange : .white.opacity(0.92))
                    .frame(width: 30, height: 30)
            }
            .buttonStyle(LiquidGlassPressStyle())

            Text(model.collapsedPillTitle)
                .font(.system(size: 12.5, weight: .semibold))
                .foregroundStyle(.white)
                .shadow(color: .black.opacity(0.85), radius: 2, x: 0, y: 0)
                .lineLimit(1)
                .frame(maxWidth: .infinity)
                .padding(.trailing, 8)
        }
        .padding(.horizontal, 6)
        .frame(minWidth: 156, maxWidth: 210, minHeight: 40, maxHeight: 40)
        .background(
            LiquidGlassShapeBackground(
                shape: Capsule(),
                tintOpacity: 0.28,
                rimOpacity: 0.32,
                specularOpacity: 0.20,
                shadowRadius: 10
            )
        )
        .contentShape(Capsule())
        .onTapGesture {
            withAnimation(.spring(response: 0.30, dampingFraction: 0.84)) {
                model.isChromeCollapsed = false
                model.updateAllWebViewContentInsets()
            }
        }
    }

    // Exact Android 2-Row + iOS 26 Safari Liquid Glass Floating Island (Video Frames 00:00, 00:04, 00:09, 00:27!)
    private var androidExpandedIsland: some View {
        VStack(spacing: 4) {
            // Row 1: address_row (46pt capsule pill: [PageTools] [Centered Domain / URL TextField] [Bookmark] [Reload/Stop])
            HStack(spacing: 2) {
                Button {
                    UIImpactFeedbackGenerator(style: .light).impactOccurred()
                    withAnimation(.spring(response: 0.34, dampingFraction: 0.80)) {
                        showBrowserMenu = false
                        showPageTools.toggle()
                    }
                } label: {
                    Image(systemName: "line.3.horizontal.decrease")
                        .font(.system(size: 16, weight: .semibold))
                        .foregroundStyle(model.isContentBlockingDisabledForCurrentHost ? .orange : androidOnSurfaceColor)
                        .frame(width: 42, height: 42)
                        .contentShape(Rectangle())
                }
                .buttonStyle(LiquidGlassPressStyle())
                .simultaneousGesture(
                    LongPressGesture(minimumDuration: 0.5).onEnded { _ in
                        UIImpactFeedbackGenerator(style: .medium).impactOccurred()
                        showToolbarEditor = true
                    }
                )

                TextField("검색 또는 주소 입력", text: $address)
                    .font(.system(size: 15, weight: addressFocused ? .regular : .medium))
                    .foregroundStyle(androidOnSurfaceColor)
                    .multilineTextAlignment(addressFocused ? .leading : .center)
                    .textInputAutocapitalization(.never)
                    .autocorrectionDisabled()
                    .keyboardType(.URL)
                    .submitLabel(.go)
                    .focused($addressFocused)
                    .onSubmit {
                        model.open(address)
                        addressFocused = false
                    }
                    .padding(.leading, 4)
                    .padding(.trailing, 4)

                if addressFocused && !address.isEmpty {
                    Button { address = "" } label: {
                        Image(systemName: "xmark.circle.fill")
                            .font(.system(size: 16))
                            .foregroundStyle(androidMutedColor)
                            .frame(width: 36, height: 42)
                            .contentShape(Rectangle())
                    }
                    .buttonStyle(LiquidGlassPressStyle())
                } else {
                    Button {
                        if !model.isStartPage {
                            showBookmarkEdit = true
                        } else {
                            showBookmarks = true
                        }
                    } label: {
                        Image(systemName: model.isCurrentBookmarked ? "bookmark.fill" : "bookmark")
                            .font(.system(size: 15, weight: .medium))
                            .foregroundStyle(model.isCurrentBookmarked ? androidAccentColor : androidOnSurfaceColor.opacity(0.9))
                            .frame(width: 36, height: 42)
                            .contentShape(Rectangle())
                    }
                    .buttonStyle(LiquidGlassPressStyle())

                    Button {
                        if model.isLoading {
                            model.stopLoading()
                        } else {
                            model.reload()
                        }
                    } label: {
                        Image(systemName: model.isLoading ? "xmark" : "arrow.clockwise")
                            .font(.system(size: 15.5, weight: .semibold))
                            .foregroundStyle(androidOnSurfaceColor)
                            .frame(width: 38, height: 42)
                            .contentShape(Rectangle())
                    }
                    .buttonStyle(LiquidGlassPressStyle())
                }
            }
            .padding(.horizontal, 6)
            .frame(height: 46)
            .background(
                Capsule()
                    .fill(model.preferences.liquidGlassUi ? Color.white.opacity(0.08) : androidFieldColor)
            )
            .overlay(
                Capsule()
                    .strokeBorder(
                        addressFocused ? androidAccentColor : (model.preferences.liquidGlassUi ? Color.white.opacity(0.18) : androidOutlineColor),
                        lineWidth: addressFocused ? 1.2 : 0.8
                    )
            )

            // Row 2: navigation_scroller -> navigation_row (46pt, horizontally scrollable & centered when fits!)
            let activeShortcuts = model.preferences.toolbarShortcuts.isEmpty
                ? ToolbarShortcutAction.defaultOrder
                : model.preferences.toolbarShortcuts
            GeometryReader { navGeo in
                ScrollView(.horizontal, showsIndicators: false) {
                    HStack(spacing: 2) {
                        ForEach(Array(activeShortcuts.enumerated()), id: \.offset) { _, shortcut in
                            androidToolbarButton(for: shortcut)
                                .frame(maxWidth: activeShortcuts.count <= 6 ? .infinity : nil)
                        }
                    }
                    .frame(minWidth: navGeo.size.width, minHeight: 46)
                }
            }
            .frame(height: 46)
        }
        .padding(.horizontal, 7)
        .padding(.vertical, 6)
        .background {
            if model.preferences.liquidGlassUi {
                LiquidGlassShapeBackground(
                    shape: RoundedRectangle(cornerRadius: 28, style: .continuous),
                    tintOpacity: 0.44,
                    rimOpacity: 0.34,
                    specularOpacity: 0.24,
                    shadowRadius: 16
                )
            } else {
                RoundedRectangle(cornerRadius: 24, style: .continuous)
                    .fill(Color(red: 0.129, green: 0.114, blue: 0.106, opacity: 0.92))
                    .overlay(
                        RoundedRectangle(cornerRadius: 24, style: .continuous)
                            .strokeBorder(androidOutlineColor, lineWidth: 1.0)
                    )
                    .shadow(color: .black.opacity(0.30), radius: 10, y: 4)
            }
        }
    }

    private func androidToolbarButton(for shortcut: ToolbarShortcutAction) -> some View {
        let isDisabled = (shortcut == .back && !model.canGoBack) || (shortcut == .forward && !model.canGoForward)
        return Button {
            triggerToolbarShortcut(shortcut)
        } label: {
            Group {
                if shortcut == .tabs {
                    // Exact Android tab_count_background.xml: 22x22 rounded-5 square with 2pt border + count
                    ZStack {
                        RoundedRectangle(cornerRadius: 5, style: .continuous)
                            .strokeBorder(androidOnSurfaceColor, lineWidth: 1.9)
                            .frame(width: 22, height: 22)
                        Text(model.visibleTabs.count > 99 ? ":D" : "\(model.visibleTabs.count)")
                            .font(.system(size: 11, weight: .bold).monospacedDigit())
                            .foregroundStyle(androidOnSurfaceColor)
                    }
                } else if shortcut == .menu {
                    Image(systemName: "ellipsis")
                        .font(.system(size: 19, weight: .semibold))
                        .foregroundStyle(androidOnSurfaceColor)
                } else {
                    Image(systemName: shortcut.systemImage)
                        .font(.system(size: 18, weight: .medium))
                        .foregroundStyle(androidOnSurfaceColor)
                }
            }
            .frame(width: 48, height: 44)
            .contentShape(Rectangle())
            .opacity(isDisabled ? 0.35 : 1.0)
        }
        .buttonStyle(LiquidGlassPressStyle())
        .disabled(isDisabled)
        .simultaneousGesture(
            LongPressGesture(minimumDuration: 0.5).onEnded { _ in
                UIImpactFeedbackGenerator(style: .medium).impactOccurred()
                showToolbarEditor = true
            }
        )
    }

    private func triggerToolbarShortcut(_ action: ToolbarShortcutAction) {
        switch action {
        case .back: model.back()
        case .forward: model.forward()
        case .home: model.home()
        case .newTab:
            _ = model.newTab()
            if model.preferences.showKeyboardOnNewTab { addressFocused = true }
        case .tabs:
            model.captureSnapshot(for: model.activeID)
            addressFocused = false
            showPageTools = false
            showBrowserMenu = false
            UIImpactFeedbackGenerator(style: .light).impactOccurred()
            withAnimation(.spring(response: 0.36, dampingFraction: 0.86)) {
                showTabs = true
            }
        case .menu:
            UIImpactFeedbackGenerator(style: .light).impactOccurred()
            withAnimation(.spring(response: 0.34, dampingFraction: 0.80)) {
                showPageTools = false
                showBrowserMenu.toggle()
            }
        case .findInPage:
            showFindBar = true
        case .contentBlocking:
            model.toggleContentBlockingForCurrentHost()
        case .desktopSite:
            model.toggleDesktopMode()
        case .extensions:
            showExtensions = true
        case .capture:
            showCaptureMenu = true
        case .watermark:
            showWatermarkRemover = true
        case .bookshelf:
            showBookshelf = true
        case .vpn:
            showNetworkPrivacy = true
        case .history:
            showHistory = true
        case .bookmarks:
            showBookmarks = true
        case .share:
            if let url = URL(string: model.currentURL), !model.isStartPage {
                sharePayload = [url]
            } else {
                model.status = "공유할 웹페이지가 없습니다."
            }
        case .copyAddress:
            UIPasteboard.general.string = model.currentURL
            model.status = "현재 주소를 클립보드에 복사했습니다."
        case .reopenClosedTab:
            model.restoreClosedTab()
        case .closeAllTabs:
            showCloseAllConfirm = true
        case .settings:
            showSettings = true
        }
    }

    private func checkUpdates() async {
        guard let url = URL(string: "https://api.github.com/repos/omyeol/Omyaru-Browser/releases/tags/ios-latest") else { return }
        do {
            var request = URLRequest(url: url)
            request.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
            let (data, _) = try await URLSession.shared.data(for: request)
            let json = try JSONSerialization.jsonObject(with: data) as? [String: Any]
            let release = json?["name"] as? String ?? ""
            let installed = Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? BrowserModel.appVersionName
            updateMessage = release.contains(installed)
                ? "최신 버전(v\(installed))입니다."
                : "새 버전: \(release). AltStore 소스에서 업데이트할 수 있습니다."
        } catch {
            updateMessage = "업데이트를 확인하지 못했습니다: \(error.localizedDescription)"
        }
    }
}

// MARK: - Page Tools Sheet (Exact Android PageToolsDialog.kt Layout: [- 5%] [100% Picker] [+ 5%] + 4-Button Grid!)

struct PageToolsSheet: View {
    @Environment(\.dismiss) private var dismiss
    @ObservedObject var model: BrowserModel
    var onStartFind: () -> Void
    var onOpenCapture: () -> Void
    var onOpenWatermark: () -> Void
    var onOpenExtensions: () -> Void
    @State private var showZoomPicker = false

    private var currentPercent: Int {
        Int(((model.activeTab?.pageZoom ?? model.preferences.fontScale) * 100).rounded())
    }

    var body: some View {
        NavigationStack {
            VStack(spacing: 16) {
                // 1. Top Section: [-] [현재 비율%] [+]
                HStack(spacing: 14) {
                    Button {
                        model.setPageZoom((model.activeTab?.pageZoom ?? model.preferences.fontScale) - 0.05)
                    } label: {
                        Image(systemName: "minus")
                            .frame(width: 44, height: 44)
                            .background(Color(red: 0.17, green: 0.17, blue: 0.20), in: Circle())
                    }

                    Button {
                        showZoomPicker = true
                    } label: {
                        Text("\(currentPercent)%")
                            .font(.headline.monospacedDigit())
                            .foregroundStyle(.blue)
                            .padding(.horizontal, 24)
                            .frame(height: 44)
                            .background(Color(red: 0.17, green: 0.17, blue: 0.20), in: Capsule())
                    }

                    Button {
                        model.setPageZoom((model.activeTab?.pageZoom ?? model.preferences.fontScale) + 0.05)
                    } label: {
                        Image(systemName: "plus")
                            .frame(width: 44, height: 44)
                            .background(Color(red: 0.17, green: 0.17, blue: 0.20), in: Circle())
                    }
                }
                .buttonStyle(.plain)
                .padding(.top, 8)

                Divider()

                // 2. 4-Column Quick Actions Grid (Exact Android PageToolsDialog Parity!)
                LazyVGrid(columns: Array(repeating: GridItem(.flexible(), spacing: 10), count: 4), spacing: 12) {
                    pageActionTile(icon: "puzzlepiece.extension", label: "확장프로그램", active: false) {
                        onOpenExtensions()
                    }
                    pageActionTile(icon: "magnifyingglass", label: "페이지 찾기", active: false) {
                        onStartFind()
                    }
                    pageActionTile(
                        icon: "shield.lefthalf.filled",
                        label: model.isContentBlockingDisabledForCurrentHost ? "차단 켜기" : "차단 끄기",
                        active: !model.isContentBlockingDisabledForCurrentHost
                    ) {
                        model.toggleContentBlockingForCurrentHost()
                        dismiss()
                    }
                    pageActionTile(
                        icon: "desktopcomputer",
                        label: model.activeTab?.isDesktopMode == true ? "모바일" : "데스크톱",
                        active: model.activeTab?.isDesktopMode == true
                    ) {
                        model.toggleDesktopMode()
                        dismiss()
                    }
                }

                // 3. Extra Tools Row
                HStack(spacing: 10) {
                    Button {
                        onOpenCapture()
                    } label: {
                        Label("영역 캡처", systemImage: "viewfinder")
                            .font(.subheadline.weight(.semibold))
                            .frame(maxWidth: .infinity)
                            .padding(.vertical, 11)
                    }
                    .buttonStyle(.bordered)

                    Button {
                        onOpenWatermark()
                    } label: {
                        Label("워터마크 제거", systemImage: "wand.and.stars")
                            .font(.subheadline.weight(.semibold))
                            .frame(maxWidth: .infinity)
                            .padding(.vertical, 11)
                    }
                    .buttonStyle(.bordered)
                }

                // 4. Media PiP & Seek Controls (Android BrowserMediaController Parity!)
                HStack(spacing: 8) {
                    let secs = model.preferences.pipSeekSeconds
                    Button {
                        model.seekActiveMedia(by: -secs)
                    } label: {
                        Label("-\(secs)초", systemImage: "gobackward")
                            .font(.caption.weight(.semibold))
                            .frame(maxWidth: .infinity)
                            .padding(.vertical, 9)
                            .background(Color(red: 0.16, green: 0.16, blue: 0.19), in: RoundedRectangle(cornerRadius: 12))
                    }
                    Button {
                        model.enterActiveVideoPiP()
                        dismiss()
                    } label: {
                        Label("PiP 전환", systemImage: "pip.enter")
                            .font(.caption.weight(.semibold))
                            .frame(maxWidth: .infinity)
                            .padding(.vertical, 9)
                            .background(Color(red: 0.16, green: 0.16, blue: 0.19), in: RoundedRectangle(cornerRadius: 12))
                    }
                    Button {
                        model.seekActiveMedia(by: secs)
                    } label: {
                        Label("+\(secs)초", systemImage: "goforward")
                            .font(.caption.weight(.semibold))
                            .frame(maxWidth: .infinity)
                            .padding(.vertical, 9)
                            .background(Color(red: 0.16, green: 0.16, blue: 0.19), in: RoundedRectangle(cornerRadius: 12))
                    }
                }
                .buttonStyle(.plain)

                Spacer()
            }
            .padding(16)
            .navigationTitle("페이지 도구")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .confirmationAction) {
                    Button("닫기") { dismiss() }
                }
            }
            .confirmationDialog("배율 선택 (50% ~ 200%)", isPresented: $showZoomPicker) {
                ForEach([50, 75, 90, 100, 110, 125, 150, 175, 200], id: \.self) { pct in
                    Button("\(pct)%") { model.setPageZoom(Double(pct) / 100.0) }
                }
                Button("취소", role: .cancel) {}
            }
        }
        .presentationDetents([.medium])
    }

    private func pageActionTile(icon: String, label: String, active: Bool, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            VStack(spacing: 8) {
                Image(systemName: icon)
                    .font(.title3)
                    .foregroundStyle(active ? .blue : .primary)
                Text(label)
                    .font(.caption2.weight(.semibold))
                    .lineLimit(1)
            }
            .frame(maxWidth: .infinity)
            .padding(.vertical, 14)
            .background(Color(red: 0.16, green: 0.16, blue: 0.19), in: RoundedRectangle(cornerRadius: 14))
        }
        .buttonStyle(.plain)
    }
}

// MARK: - iOS 26 Safari Liquid Glass Fullscreen Tab Overview (Exact Reference Video 00:01, 00:11-00:26!)

private struct TabCardFramePreferenceKey: PreferenceKey {
    static var defaultValue: [UUID: CGRect] = [:]
    static func reduce(value: inout [UUID: CGRect], nextValue: () -> [UUID: CGRect]) {
        value.merge(nextValue(), uniquingKeysWith: { _, new in new })
    }
}

struct TabOverview: View {
    @EnvironmentObject private var model: BrowserModel
    var onDismiss: () -> Void

    @Namespace private var pillNamespace
    @State private var pagedGroupID: UUID = BrowserModel.defaultGroupID
    @State private var searchMode = false
    @State private var searchQuery = ""
    @State private var selectionMode = false
    @State private var selectedTabIDs: Set<UUID> = []
    @State private var useGridLayout = true
    @State private var showTopMenu = false
    @State private var contextMenuTab: BrowserTab? = nil
    @State private var newGroupName = ""
    @State private var showingNewGroup = false
    @State private var showingGroupManager = false
    @State private var showingBatchMove = false
    @State private var batchMoveIDs: Set<UUID> = []
    @State private var showingCloseAllConfirm = false
    @State private var draggingTabID: UUID? = nil
    @State private var dragSessionID = UUID()
    @State private var dragCardThumbHeight: CGFloat = 180
    @State private var dragCardWidth: CGFloat = 160
    @State private var dragCardIsHero = false
    @State private var dragCardCenter: CGPoint = .zero
    @State private var dragCardScale: CGFloat = 1.0
    @State private var dragGrabOffset: CGSize = .zero
    @State private var dragStartLocation: CGPoint? = nil
    @State private var dragMovedBeyondThreshold = false
    @State private var lastSwapTimestamp: TimeInterval = 0
    @State private var tabFrames: [UUID: CGRect] = [:]
    @FocusState private var searchFieldFocused: Bool

    private func displayedTabs(for groupID: UUID) -> [BrowserTab] {
        let q = searchQuery.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        let raw = model.tabs(forGroup: groupID)
        let filtered = q.isEmpty ? raw : raw.filter {
            $0.title.lowercased().contains(q) || $0.url.lowercased().contains(q)
        }
        return model.preferences.tabsStartFromBottom ? Array(filtered.reversed()) : filtered
    }

    var body: some View {
        ZStack {
            // Ambient Frosted Liquid Glass Backdrop (soft radial glow over blurred live browser)
            ZStack {
                Color.black.opacity(0.36).ignoresSafeArea()
                Rectangle().fill(.ultraThinMaterial).opacity(0.72).ignoresSafeArea()
                RadialGradient(
                    colors: [
                        Color(hex: model.activeProfile.colorHex).opacity(0.16),
                        Color(red: 0.16, green: 0.11, blue: 0.14).opacity(0.24),
                        Color.black.opacity(0.45)
                    ],
                    center: .topLeading,
                    startRadius: 40,
                    endRadius: 540
                )
                .ignoresSafeArea()
            }

            VStack(spacing: 0) {
                liquidGlassTopBar

                // Exact Frames 00:15 -> 00:21: Native Interactive Horizontal Swipe Pager across ALL Groups!
                TabView(selection: $pagedGroupID) {
                    ForEach(model.workspaceGroupIDs, id: \.self) { groupID in
                        groupPageView(for: groupID)
                            .tag(groupID)
                    }
                }
                .tabViewStyle(.page(indexDisplayMode: .never))
                .scrollDisabled(draggingTabID != nil)
                .animation(.spring(response: 0.34, dampingFraction: 0.86), value: pagedGroupID)

                liquidGlassBottomCarouselBar
            }

            // Dedicated floating lifted tab card overlay (outside ScrollView/LazyVGrid -> zero gesture/offset feedback jitter!)
            if let dragID = draggingTabID,
               let draggedTab = model.tabs.first(where: { $0.id == dragID }) {
                safariTabCardVisual(
                    tab: draggedTab,
                    cardHeight: dragCardThumbHeight,
                    isHeroSingle: dragCardIsHero,
                    isLiftedOverlay: true
                )
                .frame(width: dragCardWidth)
                .scaleEffect(dragCardScale)
                .position(dragCardCenter)
                .allowsHitTesting(false)
                .zIndex(80)
            }

            // Exact Video Frames 00:22 -> 00:25: Morphing Top-Right Menu Popover with Inline Expandable Profile List!
            LiquidGlassMorphContainer(
                isPresented: showTopMenu,
                alignment: .topTrailing,
                anchor: .topTrailing,
                edgeInsets: EdgeInsets(top: 62, leading: 16, bottom: 0, trailing: 14),
                onDismiss: { showTopMenu = false }
            ) {
                LiquidGlassTabOverviewMenuPopover(
                    model: model,
                    selectionMode: selectionMode,
                    selectedCount: selectedTabIDs.count,
                    useGridLayout: useGridLayout,
                    onDismiss: {
                        withAnimation(.spring(response: 0.30, dampingFraction: 0.84)) { showTopMenu = false }
                    },
                    onManageGroups: { showingGroupManager = true },
                    onEnterSelectionMode: {
                        withAnimation(.spring(response: 0.28, dampingFraction: 0.84)) {
                            selectionMode = true
                            selectedTabIDs.removeAll()
                        }
                    },
                    onSelectAll: {
                        selectedTabIDs = Set(model.visibleTabs.map(\.id))
                    },
                    onMoveSelected: {
                        guard !selectedTabIDs.isEmpty else { return }
                        batchMoveIDs = selectedTabIDs
                        showingBatchMove = true
                    },
                    onCloseSelected: {
                        let toClose = selectedTabIDs
                        selectedTabIDs.removeAll()
                        selectionMode = false
                        withAnimation(.spring(response: 0.28, dampingFraction: 0.82)) {
                            model.closeTabs(toClose)
                        }
                    },
                    onToggleGridList: {
                        withAnimation(.spring(response: 0.28, dampingFraction: 0.84)) {
                            useGridLayout.toggle()
                        }
                    },
                    onCloseAllTabs: { showingCloseAllConfirm = true }
                )
            }

            // Exact Video Frames 00:12 & 00:14: Floating Liquid Glass Tab Card Long-Press Menu!
            LiquidGlassMorphContainer(
                isPresented: contextMenuTab != nil,
                alignment: .center,
                anchor: .center,
                edgeInsets: EdgeInsets(top: 120, leading: 24, bottom: 80, trailing: 24),
                onDismiss: { contextMenuTab = nil }
            ) {
                if let targetTab = contextMenuTab {
                    VStack(spacing: 16) {
                        // Lifted Mini Preview Card
                        VStack(spacing: 6) {
                            ZStack {
                                Color(red: 0.12, green: 0.13, blue: 0.16)
                                if let snap = model.tabSnapshots[targetTab.id] {
                                    Image(uiImage: snap)
                                        .resizable()
                                        .scaledToFill()
                                        .frame(width: 196, height: 150)
                                        .clipped()
                                } else {
                                    Image(systemName: targetTab.url == "about:blank" ? "sparkles" : "globe")
                                        .font(.largeTitle)
                                        .foregroundStyle(.white.opacity(0.45))
                                }
                            }
                            .frame(width: 196, height: 150)
                            .clipShape(RoundedRectangle(cornerRadius: 18, style: .continuous))
                            .overlay(
                                RoundedRectangle(cornerRadius: 18, style: .continuous)
                                    .strokeBorder(Color.white.opacity(0.28), lineWidth: 1)
                            )
                            .shadow(color: .black.opacity(0.48), radius: 22, y: 10)

                            Text(targetTab.title)
                                .font(.caption.weight(.semibold))
                                .foregroundStyle(.white)
                                .lineLimit(1)
                                .frame(maxWidth: 220)
                        }

                        LiquidGlassTabCardContextPopover(
                            tab: targetTab,
                            onDismiss: {
                                withAnimation(.spring(response: 0.28, dampingFraction: 0.84)) { contextMenuTab = nil }
                            },
                            onCopyLink: {
                                UIPasteboard.general.string = targetTab.url
                                model.status = "링크를 복사했습니다."
                            },
                            onTogglePin: {
                                withAnimation(.spring(response: 0.28, dampingFraction: 0.84)) {
                                    model.togglePinTab(targetTab.id)
                                }
                            },
                            onDuplicate: {
                                withAnimation(.spring(response: 0.28, dampingFraction: 0.82)) {
                                    model.duplicateTab(targetTab.id)
                                }
                            },
                            onSelectTab: {
                                withAnimation(.spring(response: 0.28, dampingFraction: 0.84)) {
                                    selectionMode = true
                                    selectedTabIDs.insert(targetTab.id)
                                }
                            },
                            onMoveTab: {
                                batchMoveIDs = [targetTab.id]
                                showingBatchMove = true
                            },
                            onCloseOtherTabs: {
                                withAnimation(.spring(response: 0.28, dampingFraction: 0.82)) {
                                    model.closeOtherTabs(keeping: targetTab.id)
                                }
                            },
                            onCloseTab: {
                                withAnimation(.spring(response: 0.28, dampingFraction: 0.82)) {
                                    model.close(targetTab.id)
                                }
                            }
                        )
                    }
                }
            }
        }
        .coordinateSpace(name: "overviewRootSpace")
        .onAppear {
            pagedGroupID = model.activeGroupID
        }
        .onChange(of: pagedGroupID) { nextGroup in
            if nextGroup != model.activeGroupID {
                UIImpactFeedbackGenerator(style: .light).impactOccurred()
                model.selectGroup(nextGroup)
            }
        }
        .onChange(of: model.activeGroupID) { nextGroup in
            if pagedGroupID != nextGroup {
                withAnimation(.spring(response: 0.34, dampingFraction: 0.86)) {
                    pagedGroupID = nextGroup
                }
            }
        }
        .alert("새 탭 그룹", isPresented: $showingNewGroup) {
            TextField("그룹 이름", text: $newGroupName)
            Button("만들기") { model.addGroup(newGroupName); newGroupName = "" }
            Button("취소", role: .cancel) { newGroupName = "" }
        }
        .sheet(isPresented: $showingGroupManager) {
            TabGroupManagerSheet(model: model)
        }
        .sheet(isPresented: $showingBatchMove) {
            TabBatchMoveSheet(model: model, tabIDs: batchMoveIDs) {
                selectedTabIDs.removeAll()
                selectionMode = false
            }
        }
        .sheet(isPresented: $showingCloseAllConfirm) {
            CloseAllTabsCountdownSheet(tabCount: model.visibleTabs.count) {
                model.closeAllVisibleTabs()
                onDismiss()
            }
        }
    }

    // Exact Video Top Bar: Floating Liquid Glass Circles [🔍] [≡] ... [⋯]
    private var liquidGlassTopBar: some View {
        ZStack {
            HStack(spacing: 10) {
                LiquidGlassCircleButton(systemImage: selectionMode ? "checkmark" : "magnifyingglass", size: 42, iconSize: 16) {
                    if selectionMode {
                        if selectedTabIDs.count == model.visibleTabs.count {
                            selectedTabIDs.removeAll()
                        } else {
                            selectedTabIDs = Set(model.visibleTabs.map(\.id))
                        }
                    } else {
                        withAnimation(.spring(response: 0.30, dampingFraction: 0.84)) {
                            searchMode = true
                        }
                        searchFieldFocused = true
                    }
                }

                if !selectionMode {
                    LiquidGlassCircleButton(
                        systemImage: useGridLayout ? "line.3.horizontal.decrease" : "square.grid.2x2",
                        size: 42,
                        iconSize: 15
                    ) {
                        withAnimation(.spring(response: 0.30, dampingFraction: 0.84)) {
                            useGridLayout.toggle()
                        }
                    }
                }

                Spacer()

                if selectionMode {
                    Text("\(selectedTabIDs.count)개 선택됨")
                        .font(.subheadline.weight(.bold))
                        .foregroundStyle(.white)
                        .padding(.horizontal, 16)
                        .frame(height: 38)
                        .background(LiquidGlassShapeBackground(shape: Capsule(), tintOpacity: 0.42, rimOpacity: 0.34, shadowRadius: 8))
                    Spacer()
                }

                LiquidGlassCircleButton(systemImage: "ellipsis", size: 42, iconSize: 17) {
                    UIImpactFeedbackGenerator(style: .light).impactOccurred()
                    withAnimation(.spring(response: 0.34, dampingFraction: 0.80)) {
                        showTopMenu.toggle()
                    }
                }
            }
            .opacity(searchMode && !selectionMode ? 0 : 1)

            if searchMode && !selectionMode {
                HStack(spacing: 8) {
                    LiquidGlassCircleButton(systemImage: "chevron.left", size: 42, iconSize: 16) {
                        withAnimation(.spring(response: 0.28, dampingFraction: 0.84)) {
                            searchMode = false
                            searchQuery = ""
                            searchFieldFocused = false
                        }
                    }

                    TextField("탭 제목 또는 주소 검색", text: $searchQuery)
                        .font(.subheadline)
                        .foregroundStyle(.white)
                        .focused($searchFieldFocused)
                        .padding(.horizontal, 16)
                        .frame(height: 42)
                        .background(LiquidGlassShapeBackground(shape: Capsule(), tintOpacity: 0.46, rimOpacity: 0.34, shadowRadius: 10))

                    LiquidGlassCircleButton(systemImage: "xmark", size: 42, iconSize: 15) {
                        if !searchQuery.isEmpty {
                            searchQuery = ""
                        } else {
                            withAnimation(.spring(response: 0.28, dampingFraction: 0.84)) {
                                searchMode = false
                                searchFieldFocused = false
                            }
                        }
                    }
                }
                .transition(.opacity.combined(with: .scale(scale: 0.96)))
            }
        }
        .padding(.horizontal, 14)
        .padding(.top, 8)
        .padding(.bottom, 6)
    }

    // Group Page Content (Bold Group Header + Centered Single Hero Card OR Responsive Liquid Glass Grid)
    private func groupPageView(for groupID: UUID) -> some View {
        let groupTabs = displayedTabs(for: groupID)
        let headerTitle = model.groupHeaderTitle(for: groupID)
        let colCount = min(10, max(1, model.preferences.tabGridColumns))
        let ratio = model.preferences.tabThumbnailAspectRatio.heightRatio

        return GeometryReader { outerGeo in
            ScrollViewReader { proxy in
                ScrollView(showsIndicators: false) {
                    VStack(alignment: .leading, spacing: 12) {
                        // Exact Video Frames 00:16 -> 00:20: Large Bold Group Title ("무제", "만화", "만화 어허")
                        if let headerTitle {
                            Text(headerTitle)
                                .font(.system(size: 28, weight: .bold))
                                .foregroundStyle(.white)
                                .padding(.horizontal, 16)
                                .padding(.top, 4)
                        }

                        if groupTabs.isEmpty {
                            VStack(spacing: 10) {
                                Image(systemName: groupID == BrowserModel.privateGroupID ? "hand.raised.fill" : "square.on.square")
                                    .font(.system(size: 36))
                                    .foregroundStyle(.white.opacity(0.4))
                                Text(searchQuery.isEmpty ? "열려 있는 탭이 없습니다." : "일치하는 탭이 없습니다.")
                                    .font(.subheadline)
                                    .foregroundStyle(.white.opacity(0.65))
                            }
                            .frame(maxWidth: .infinity)
                            .padding(.top, 110)
                        } else if !useGridLayout {
                            VStack(spacing: 8) {
                                ForEach(groupTabs) { tab in
                                    liquidGlassListRow(tab: tab)
                                }
                            }
                            .padding(.horizontal, 14)
                        } else if groupTabs.count == 1 && headerTitle == nil && !selectionMode {
                            // Exact Video Frame 00:01: Signature Single Large Centered Hero Card!
                            HStack {
                                Spacer(minLength: 0)
                                let heroW = min(outerGeo.size.width * 0.68, 285)
                                let heroH = min(outerGeo.size.height * 0.60, heroW * 1.38)
                                safariTabCard(tab: groupTabs[0], cardHeight: heroH, isHeroSingle: true, outerGeo: outerGeo, scrollProxy: proxy, groupTabs: groupTabs)
                                    .frame(width: heroW)
                                Spacer(minLength: 0)
                            }
                            .padding(.top, 16)
                        } else {
                            let cellWidth = max(46, (outerGeo.size.width - 28 - CGFloat(colCount - 1) * 12) / CGFloat(colCount))
                            let thumbHeight = min(290, max(78, cellWidth * ratio))
                            VStack {
                                if model.preferences.tabsStartFromBottom {
                                    Spacer(minLength: 0)
                                }
                                LazyVGrid(columns: Array(repeating: GridItem(.flexible(), spacing: 12), count: colCount), spacing: 14) {
                                    ForEach(groupTabs) { tab in
                                        safariTabCard(tab: tab, cardHeight: thumbHeight, isHeroSingle: false, outerGeo: outerGeo, scrollProxy: proxy, groupTabs: groupTabs)
                                            .id(tab.id)
                                            .zIndex(draggingTabID == tab.id ? 100 : 0)
                                            .transition(.asymmetric(
                                                insertion: .scale(scale: 0.90).combined(with: .opacity),
                                                removal: .scale(scale: 0.72).combined(with: .opacity)
                                            ))
                                    }
                                }
                                .padding(.horizontal, 14)
                                .padding(.bottom, 16)
                                .animation(.spring(response: 0.32, dampingFraction: 0.78), value: groupTabs.map(\.id))
                            }
                            .frame(minHeight: max(0, outerGeo.size.height - (headerTitle == nil ? 12 : 54)))
                        }
                    }
                    .padding(.bottom, 12)
                }
                .scrollDisabled(draggingTabID != nil)
                .onPreferenceChange(TabCardFramePreferenceKey.self) { frames in
                    for (id, rect) in frames {
                        tabFrames[id] = rect
                    }
                }
            }
        }
    }

    // Pure visual body shared by grid card and lifted floating drag overlay
    private func safariTabCardVisual(
        tab: BrowserTab,
        cardHeight: CGFloat,
        isHeroSingle: Bool,
        isLiftedOverlay: Bool
    ) -> some View {
        let isActive = tab.id == model.activeID
        let isSelected = selectedTabIDs.contains(tab.id)
        let host = URL(string: tab.url)?.host?.replacingOccurrences(of: "^www\\.", with: "", options: .regularExpression)
        let cardLabel = tab.title.isEmpty || tab.title == "새 탭" ? (host ?? "새 탭") : tab.title

        return VStack(spacing: 7) {
            ZStack(alignment: .topTrailing) {
                Group {
                    if let snap = model.tabSnapshots[tab.id] {
                        Image(uiImage: snap)
                            .resizable()
                            .scaledToFill()
                            .frame(maxWidth: .infinity)
                            .frame(height: cardHeight)
                            .clipped()
                    } else {
                        ZStack {
                            Color(red: 0.08, green: 0.09, blue: 0.11)
                            Image(systemName: tab.url == "about:blank" ? "sparkles" : "safari")
                                .font(.system(size: isHeroSingle ? 40 : 28))
                                .foregroundStyle(.white.opacity(0.22))
                        }
                        .frame(maxWidth: .infinity)
                        .frame(height: cardHeight)
                    }
                }
                .clipShape(RoundedRectangle(cornerRadius: isHeroSingle ? 22 : 18, style: .continuous))
                .overlay(
                    RoundedRectangle(cornerRadius: isHeroSingle ? 22 : 18, style: .continuous)
                        .strokeBorder(
                            isLiftedOverlay
                                ? Color.white.opacity(0.75)
                                : (selectionMode && isSelected)
                                    ? Color(red: 0.20, green: 0.56, blue: 1.0)
                                    : (isActive ? Color.white.opacity(0.55) : Color.white.opacity(0.14)),
                            lineWidth: (isLiftedOverlay || isActive || (selectionMode && isSelected)) ? 2.0 : 0.9
                        )
                )
                .shadow(
                    color: .black.opacity(isLiftedOverlay ? 0.62 : 0.40),
                    radius: isLiftedOverlay ? 26 : (isHeroSingle ? 18 : 10),
                    y: isLiftedOverlay ? 14 : 6
                )

                // Pinned Badge Top-Left
                if tab.isPinned {
                    HStack {
                        Image(systemName: "pin.fill")
                            .font(.system(size: 10, weight: .bold))
                            .foregroundStyle(.white)
                            .padding(5)
                            .background(Color.blue.opacity(0.85), in: Circle())
                            .padding(8)
                        Spacer()
                    }
                }

                // Selection Checkmark or Floating Close (×) Circle at Top-Right
                if selectionMode {
                    Image(systemName: isSelected ? "checkmark.circle.fill" : "circle")
                        .font(.system(size: 22))
                        .foregroundStyle(isSelected ? Color(red: 0.20, green: 0.56, blue: 1.0) : .white.opacity(0.75))
                        .padding(8)
                } else if !isHeroSingle && !isLiftedOverlay {
                    Button {
                        UIImpactFeedbackGenerator(style: .light).impactOccurred()
                        withAnimation(.spring(response: 0.28, dampingFraction: 0.82)) {
                            model.close(tab.id)
                        }
                    } label: {
                        Image(systemName: "xmark")
                            .font(.system(size: 10, weight: .bold))
                            .foregroundStyle(.white.opacity(0.92))
                            .frame(width: 24, height: 24)
                            .background(Color.black.opacity(0.58), in: Circle())
                            .overlay(Circle().strokeBorder(Color.white.opacity(0.20), lineWidth: 0.6))
                    }
                    .buttonStyle(LiquidGlassPressStyle())
                    .padding(7)
                }
            }

            // Icon + Title cleanly BELOW the Card (Exact Video Frames 00:01 & 00:16-00:20!)
            HStack(spacing: 5) {
                if isHeroSingle { Spacer(minLength: 0) }
                ZStack {
                    Circle()
                        .fill(Color.white.opacity(0.16))
                        .frame(width: 15, height: 15)
                    Image(systemName: tab.isPrivate ? "hand.raised.fill" : "globe")
                        .font(.system(size: 9, weight: .semibold))
                        .foregroundStyle(.white.opacity(0.85))
                }
                Text(cardLabel)
                    .font(.system(size: isHeroSingle ? 13 : 11.5, weight: .medium))
                    .foregroundStyle(.white.opacity(0.92))
                    .lineLimit(1)
                if isHeroSingle { Spacer(minLength: 0) }
            }
            .padding(.horizontal, 4)
        }
    }

    // Exact Video Frames 00:01 & 00:16-00:20 Tab Card (Static un-transformed slot -> zero jitter!)
    private func safariTabCard(
        tab: BrowserTab,
        cardHeight: CGFloat,
        isHeroSingle: Bool,
        outerGeo: GeometryProxy,
        scrollProxy: ScrollViewProxy,
        groupTabs: [BrowserTab]
    ) -> some View {
        let isDraggingThis = draggingTabID == tab.id

        return safariTabCardVisual(
            tab: tab,
            cardHeight: cardHeight,
            isHeroSingle: isHeroSingle,
            isLiftedOverlay: false
        )
        .opacity(isDraggingThis ? 0.0 : 1.0)
        .background(
            GeometryReader { geo in
                Color.clear.preference(
                    key: TabCardFramePreferenceKey.self,
                    value: [tab.id: geo.frame(in: .named("overviewRootSpace"))]
                )
            }
        )
        .contentShape(Rectangle())
        .onTapGesture {
            guard draggingTabID == nil else { return }
            if selectionMode {
                if !selectedTabIDs.insert(tab.id).inserted {
                    selectedTabIDs.remove(tab.id)
                }
            } else {
                model.select(tab.id)
                onDismiss()
            }
        }
        // Unified iOS 26 Safari gesture on the STATIC un-transformed slot:
        // Hold >= 0.28s lifts floating overlay card cleanly with ZERO feedback jitter;
        // drag > 10pt glides 1:1 with finger & spring-swaps neighbors;
        // release without moving opens Liquid Glass context menu (링크 복사하기, 탭 고정 등)!
        .gesture(
            LongPressGesture(minimumDuration: 0.28)
                .sequenced(before: DragGesture(minimumDistance: 0, coordinateSpace: .named("overviewRootSpace")))
                .onChanged { value in
                    guard !selectionMode && contextMenuTab == nil else { return }
                    guard draggingTabID == nil || draggingTabID == tab.id else { return }
                    switch value {
                    case .first(true):
                        break
                    case .second(true, let drag):
                        if draggingTabID != tab.id {
                            let frame = tabFrames[tab.id] ?? CGRect(
                                x: outerGeo.size.width * 0.5 - 80,
                                y: outerGeo.size.height * 0.5 - cardHeight * 0.5,
                                width: 160,
                                height: cardHeight + 24
                            )
                            dragSessionID = UUID()
                            dragCardWidth = frame.width
                            dragCardThumbHeight = cardHeight
                            dragCardIsHero = isHeroSingle
                            dragCardCenter = CGPoint(x: frame.midX, y: frame.midY)
                            dragCardScale = 1.0
                            dragMovedBeyondThreshold = false
                            if let drag {
                                dragStartLocation = drag.startLocation
                                dragGrabOffset = CGSize(
                                    width: drag.startLocation.x - frame.midX,
                                    height: drag.startLocation.y - frame.midY
                                )
                            } else {
                                dragStartLocation = nil
                                dragGrabOffset = .zero
                            }
                            draggingTabID = tab.id
                            UIImpactFeedbackGenerator(style: .medium).impactOccurred()
                            withAnimation(.spring(response: 0.24, dampingFraction: 0.78)) {
                                dragCardScale = 1.06
                            }
                        }
                        if let drag {
                            if dragStartLocation == nil {
                                dragStartLocation = drag.startLocation
                                dragGrabOffset = CGSize(
                                    width: drag.startLocation.x - dragCardCenter.x,
                                    height: drag.startLocation.y - dragCardCenter.y
                                )
                            }
                            if let start = dragStartLocation {
                                let dist = hypot(drag.location.x - start.x, drag.location.y - start.y)
                                if dist > 10 {
                                    dragMovedBeyondThreshold = true
                                }
                            }
                            // Only move floating overlay once finger actually leaves the 10pt hold deadzone!
                            if dragMovedBeyondThreshold {
                                dragCardCenter = CGPoint(
                                    x: drag.location.x - dragGrabOffset.width,
                                    y: drag.location.y - dragGrabOffset.height
                                )
                                if !isHeroSingle && !searchMode {
                                    let now = Date().timeIntervalSinceReferenceDate
                                    if now - lastSwapTimestamp > 0.24 {
                                        if let targetTab = groupTabs.first(where: { candidate in
                                            candidate.id != tab.id &&
                                            candidate.isPinned == tab.isPinned &&
                                            (tabFrames[candidate.id]?.insetBy(dx: 16, dy: 16).contains(dragCardCenter) ?? false)
                                        }) {
                                            lastSwapTimestamp = now
                                            UIImpactFeedbackGenerator(style: .light).impactOccurred()
                                            withAnimation(.spring(response: 0.30, dampingFraction: 0.80)) {
                                                model.moveVisibleTab(fromID: tab.id, toID: targetTab.id)
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    default:
                        break
                    }
                }
                .onEnded { value in
                    let wasLifted = draggingTabID == tab.id
                    let wasDragged = dragMovedBeyondThreshold
                    dragStartLocation = nil
                    dragGrabOffset = .zero
                    dragMovedBeyondThreshold = false

                    let finishedFirstOnly: Bool
                    if case .first(true) = value {
                        finishedFirstOnly = true
                    } else {
                        finishedFirstOnly = false
                    }

                    if wasLifted && wasDragged {
                        let token = dragSessionID
                        let settleTarget = tabFrames[tab.id].map { CGPoint(x: $0.midX, y: $0.midY) }
                        withAnimation(.spring(response: 0.22, dampingFraction: 0.84)) {
                            if let settleTarget {
                                dragCardCenter = settleTarget
                            }
                            dragCardScale = 1.0
                        }
                        DispatchQueue.main.asyncAfter(deadline: .now() + 0.20) {
                            if dragSessionID == token && draggingTabID == tab.id {
                                draggingTabID = nil
                            }
                        }
                    } else if wasLifted || finishedFirstOnly {
                        draggingTabID = nil
                        dragCardScale = 1.0
                        UIImpactFeedbackGenerator(style: .soft).impactOccurred()
                        withAnimation(.spring(response: 0.32, dampingFraction: 0.82)) {
                            contextMenuTab = tab
                        }
                    } else {
                        draggingTabID = nil
                        dragCardScale = 1.0
                    }
                }
        )
    }

    private func liquidGlassListRow(tab: BrowserTab) -> some View {
        let isActive = tab.id == model.activeID
        let isSelected = selectedTabIDs.contains(tab.id)
        return HStack(spacing: 12) {
            if selectionMode {
                Image(systemName: isSelected ? "checkmark.circle.fill" : "circle")
                    .foregroundStyle(isSelected ? Color(red: 0.20, green: 0.56, blue: 1.0) : .white.opacity(0.6))
            } else if tab.isPinned {
                Image(systemName: "pin.fill").font(.caption).foregroundStyle(.blue)
            } else {
                Circle()
                    .fill(isActive ? Color(red: 0.35, green: 0.65, blue: 1.0) : Color.white.opacity(0.35))
                    .frame(width: 8, height: 8)
            }
            VStack(alignment: .leading, spacing: 2) {
                Text(tab.title).font(.subheadline.weight(.semibold)).foregroundStyle(.white).lineLimit(1)
                Text(tab.url).font(.caption2).foregroundStyle(.white.opacity(0.62)).lineLimit(1)
            }
            Spacer()
            if !selectionMode {
                Button {
                    withAnimation(.spring(response: 0.28, dampingFraction: 0.82)) { model.duplicateTab(tab.id) }
                } label: { Image(systemName: "plus.square.on.square").foregroundStyle(.white.opacity(0.75)) }
                .buttonStyle(LiquidGlassPressStyle())
                Button {
                    withAnimation(.spring(response: 0.28, dampingFraction: 0.82)) { model.close(tab.id) }
                } label: { Image(systemName: "xmark.circle.fill").foregroundStyle(.white.opacity(0.65)) }
                .buttonStyle(LiquidGlassPressStyle())
            }
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 12)
        .background(
            LiquidGlassShapeBackground(shape: RoundedRectangle(cornerRadius: 16, style: .continuous), tintOpacity: 0.40, rimOpacity: 0.24, shadowRadius: 6)
        )
        .contentShape(Rectangle())
        .onTapGesture {
            if selectionMode {
                if !selectedTabIDs.insert(tab.id).inserted { selectedTabIDs.remove(tab.id) }
            } else {
                model.select(tab.id)
                onDismiss()
            }
        }
        .onLongPressGesture(minimumDuration: 0.42) {
            guard !selectionMode else { return }
            UIImpactFeedbackGenerator(style: .medium).impactOccurred()
            withAnimation(.spring(response: 0.34, dampingFraction: 0.80)) {
                contextMenuTab = tab
            }
        }
    }

    // Exact Video Frames 00:01 & 00:15-00:21 Bottom Bar:
    // [ (＋) Glass Circle ]  [ Liquid Glass Group Carousel Pill with Sliding Active Capsule & Soft Edge Fade ]  [ (✓) Blue Circle ]
    private var liquidGlassBottomCarouselBar: some View {
        HStack(spacing: 10) {
            if selectionMode {
                LiquidGlassCircleButton(systemImage: "folder.badge.gearshape", size: 48, iconSize: 18) {
                    guard !selectedTabIDs.isEmpty else { return }
                    batchMoveIDs = selectedTabIDs
                    showingBatchMove = true
                }
                .opacity(selectedTabIDs.isEmpty ? 0.45 : 1)
            } else {
                LiquidGlassCircleButton(systemImage: "plus", size: 48, iconSize: 20) {
                    UIImpactFeedbackGenerator(style: .light).impactOccurred()
                    _ = model.newTab()
                    onDismiss()
                }
            }

            // Center Liquid Glass Group Carousel Pill with Soft Edge Fade Mask!
            if selectionMode {
                Text("\(selectedTabIDs.count)개 선택됨")
                    .font(.system(size: 14.5, weight: .semibold))
                    .foregroundStyle(.white)
                    .frame(maxWidth: .infinity)
                    .frame(height: 46)
                    .background(LiquidGlassShapeBackground(shape: Capsule(), tintOpacity: 0.38, rimOpacity: 0.34, shadowRadius: 12))
            } else {
                ScrollViewReader { stripProxy in
                    ScrollView(.horizontal, showsIndicators: false) {
                        HStack(spacing: 4) {
                            ForEach(model.workspaceGroupIDs, id: \.self) { groupID in
                                let isCurrent = pagedGroupID == groupID
                                Button {
                                    if isCurrent {
                                        showingGroupManager = true
                                    } else {
                                        UIImpactFeedbackGenerator(style: .light).impactOccurred()
                                        withAnimation(.spring(response: 0.34, dampingFraction: 0.82)) {
                                            pagedGroupID = groupID
                                        }
                                    }
                                } label: {
                                    Text(model.groupDisplayName(for: groupID))
                                        .font(.system(size: 14, weight: isCurrent ? .semibold : .medium))
                                        .foregroundStyle(isCurrent ? .white : .white.opacity(0.60))
                                        .lineLimit(1)
                                        .padding(.horizontal, 14)
                                        .frame(height: 36)
                                        .background {
                                            if isCurrent {
                                                Capsule()
                                                    .fill(Color.white.opacity(0.22))
                                                    .overlay(
                                                        Capsule()
                                                            .strokeBorder(Color.white.opacity(0.32), lineWidth: 0.8)
                                                    )
                                                    .matchedGeometryEffect(id: "activeGroupCapsule", in: pillNamespace)
                                            }
                                        }
                                }
                                .buttonStyle(.plain)
                                .id(groupID)
                            }
                        }
                        .padding(.horizontal, 26)
                        .frame(minHeight: 46)
                    }
                    .onAppear {
                        DispatchQueue.main.async {
                            stripProxy.scrollTo(pagedGroupID, anchor: .center)
                        }
                    }
                    .onChange(of: pagedGroupID) { target in
                        withAnimation(.spring(response: 0.34, dampingFraction: 0.84)) {
                            stripProxy.scrollTo(target, anchor: .center)
                        }
                    }
                }
                .mask(
                    LinearGradient(
                        stops: [
                            .init(color: .clear, location: 0.0),
                            .init(color: .black, location: 0.12),
                            .init(color: .black, location: 0.88),
                            .init(color: .clear, location: 1.0)
                        ],
                        startPoint: .leading,
                        endPoint: .trailing
                    )
                )
                .frame(height: 46)
                .background(
                    LiquidGlassShapeBackground(shape: Capsule(), tintOpacity: 0.36, rimOpacity: 0.34, specularOpacity: 0.22, shadowRadius: 12)
                )
                .clipShape(Capsule())
            }

            // Vibrant iOS 26 Safari Blue Checkmark Circle (✓)
            LiquidGlassCircleButton(
                systemImage: "checkmark",
                size: 48,
                iconSize: 18,
                tint: Color(red: 0.08, green: 0.48, blue: 1.0)
            ) {
                UIImpactFeedbackGenerator(style: .light).impactOccurred()
                if selectionMode {
                    withAnimation(.spring(response: 0.28, dampingFraction: 0.84)) {
                        selectionMode = false
                        selectedTabIDs.removeAll()
                    }
                } else {
                    onDismiss()
                }
            }
        }
        .padding(.horizontal, 14)
        .padding(.top, 6)
        .padding(.bottom, 10)
    }
}

// MARK: - Hierarchical Bookmarks Sheet (Android BookmarksDialog.kt + BookmarkEditDialog.kt Parity!)

struct BookmarksSheet: View {
    @EnvironmentObject private var model: BrowserModel
    @Environment(\.dismiss) private var dismiss
    @State private var currentFolderID: String? = nil
    @State private var search = ""
    @State private var showAddFolder = false
    @State private var newFolderName = ""
    @State private var editingBookmark: BrowserBookmark? = nil
    @State private var editTitle = ""
    @State private var editURL = ""
    @State private var showAddBookmark = false

    private var currentFolderName: String {
        guard let id = currentFolderID,
              let folder = model.bookmarkFolders.first(where: { $0.id == id }) else {
            return "북마크"
        }
        return folder.name
    }

    private var childFolders: [BookmarkFolder] {
        guard search.isEmpty else { return [] }
        return model.bookmarkFolders.filter { $0.parentID == currentFolderID }
    }

    private var visibleBookmarks: [BrowserBookmark] {
        let q = search.trimmingCharacters(in: .whitespaces).lowercased()
        if !q.isEmpty {
            return model.bookmarks.filter { $0.title.lowercased().contains(q) || $0.url.lowercased().contains(q) }
        }
        return model.bookmarks.filter { $0.folderID == currentFolderID }
    }

    var body: some View {
        NavigationStack {
            VStack(spacing: 0) {
                HStack(spacing: 8) {
                    if currentFolderID != nil {
                        Button {
                            currentFolderID = model.bookmarkFolders.first(where: { $0.id == currentFolderID })?.parentID
                        } label: {
                            Label("상위 폴더", systemImage: "chevron.left")
                                .font(.caption.weight(.semibold))
                                .padding(.horizontal, 10)
                                .padding(.vertical, 6)
                                .background(Color(red: 0.17, green: 0.17, blue: 0.20), in: Capsule())
                        }
                        .buttonStyle(.plain)
                    }
                    Button {
                        newFolderName = ""
                        showAddFolder = true
                    } label: {
                        Label("새 폴더", systemImage: "folder.badge.plus")
                            .font(.caption.weight(.semibold))
                            .frame(maxWidth: .infinity)
                            .padding(.vertical, 7)
                            .background(Color(red: 0.16, green: 0.16, blue: 0.19), in: RoundedRectangle(cornerRadius: 10))
                    }
                    .buttonStyle(.plain)

                    Button {
                        editTitle = model.activeTab?.title ?? ""
                        editURL = model.currentURL == "about:blank" ? "" : model.currentURL
                        showAddBookmark = true
                    } label: {
                        Label("현재 페이지 북마크", systemImage: "star.fill")
                            .font(.caption.weight(.semibold))
                            .foregroundStyle(.blue)
                            .frame(maxWidth: .infinity)
                            .padding(.vertical, 7)
                            .background(Color(red: 0.16, green: 0.16, blue: 0.19), in: RoundedRectangle(cornerRadius: 10))
                    }
                    .buttonStyle(.plain)
                }
                .padding(.horizontal, 12)
                .padding(.vertical, 8)

                List {
                    ForEach(childFolders) { folder in
                        Button {
                            currentFolderID = folder.id
                        } label: {
                            HStack {
                                Image(systemName: "folder.fill").foregroundStyle(.blue)
                                Text(folder.name).font(.subheadline.weight(.semibold))
                                Spacer()
                                Image(systemName: "chevron.right").font(.caption).foregroundStyle(.secondary)
                            }
                        }
                        .swipeActions {
                            Button("삭제", role: .destructive) {
                                model.bookmarkFolders.removeAll { $0.id == folder.id }
                                for i in model.bookmarks.indices where model.bookmarks[i].folderID == folder.id {
                                    model.bookmarks[i].folderID = folder.parentID
                                }
                                model.persist()
                            }
                        }
                    }

                    ForEach(visibleBookmarks) { item in
                        Button {
                            model.open(item.url)
                            dismiss()
                        } label: {
                            VStack(alignment: .leading, spacing: 2) {
                                Text(item.title).font(.subheadline.weight(.semibold)).lineLimit(1)
                                Text(item.url).font(.caption2).foregroundStyle(.secondary).lineLimit(1)
                            }
                        }
                        .contextMenu {
                            Button {
                                editingBookmark = item
                                editTitle = item.title
                                editURL = item.url
                            } label: { Label("편집", systemImage: "pencil") }
                            Button(role: .destructive) {
                                model.bookmarks.removeAll { $0.id == item.id }
                                model.persist()
                            } label: { Label("삭제", systemImage: "trash") }
                        }
                        .swipeActions {
                            Button("삭제", role: .destructive) {
                                model.bookmarks.removeAll { $0.id == item.id }
                                model.persist()
                            }
                        }
                    }
                }
                .listStyle(.plain)
            }
            .searchable(text: $search, prompt: "북마크 검색")
            .navigationTitle(currentFolderName)
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .confirmationAction) { Button("완료") { dismiss() } }
            }
            .alert("새 북마크 폴더", isPresented: $showAddFolder) {
                TextField("폴더 이름", text: $newFolderName)
                Button("만들기") {
                    let trimmed = newFolderName.trimmingCharacters(in: .whitespacesAndNewlines)
                    guard !trimmed.isEmpty else { return }
                    model.bookmarkFolders.append(BookmarkFolder(name: trimmed, parentID: currentFolderID))
                    model.persist()
                }
                Button("취소", role: .cancel) {}
            }
            .alert("북마크 추가", isPresented: $showAddBookmark) {
                TextField("제목", text: $editTitle)
                TextField("URL", text: $editURL)
                    .textInputAutocapitalization(.never)
                    .autocorrectionDisabled()
                Button("추가") {
                    guard !editURL.isEmpty else { return }
                    let u = editURL.contains("://") ? editURL : "https://" + editURL
                    model.bookmarks.append(BrowserBookmark(title: editTitle.isEmpty ? u : editTitle, url: u, folderID: currentFolderID))
                    model.persist()
                }
                Button("취소", role: .cancel) {}
            }
            .alert("북마크 편집", isPresented: Binding(get: { editingBookmark != nil }, set: { if !$0 { editingBookmark = nil } })) {
                TextField("제목", text: $editTitle)
                TextField("URL", text: $editURL)
                Button("저장") {
                    if let target = editingBookmark, let idx = model.bookmarks.firstIndex(where: { $0.id == target.id }) {
                        model.bookmarks[idx].title = editTitle.isEmpty ? editURL : editTitle
                        model.bookmarks[idx].url = editURL.contains("://") ? editURL : "https://" + editURL
                        model.persist()
                    }
                    editingBookmark = nil
                }
                Button("취소", role: .cancel) { editingBookmark = nil }
            }
        }
    }
}

// MARK: - Date-Grouped History Sheet (Android HistoryDialog.kt Parity!)

struct HistorySheet: View {
    @EnvironmentObject private var model: BrowserModel
    @Environment(\.dismiss) private var dismiss
    @State private var search = ""
    @State private var confirmClear = false

    private var filtered: [HistoryEntry] {
        let q = search.trimmingCharacters(in: .whitespaces).lowercased()
        guard !q.isEmpty else { return model.history }
        return model.history.filter { $0.title.lowercased().contains(q) || $0.url.lowercased().contains(q) }
    }

    private func groupedHistory() -> [(title: String, items: [HistoryEntry])] {
        let cal = Calendar.current
        let now = Date()
        var today: [HistoryEntry] = []
        var yesterday: [HistoryEntry] = []
        var thisWeek: [HistoryEntry] = []
        var older: [HistoryEntry] = []
        for entry in filtered {
            let d = Date(timeIntervalSince1970: Double(entry.visitedAtEpochMs) / 1000.0)
            if cal.isDateInToday(d) { today.append(entry) }
            else if cal.isDateInYesterday(d) { yesterday.append(entry) }
            else if let days = cal.dateComponents([.day], from: d, to: now).day, days <= 7 { thisWeek.append(entry) }
            else { older.append(entry) }
        }
        var result: [(String, [HistoryEntry])] = []
        if !today.isEmpty { result.append(("오늘", today)) }
        if !yesterday.isEmpty { result.append(("어제", yesterday)) }
        if !thisWeek.isEmpty { result.append(("이번 주", thisWeek)) }
        if !older.isEmpty { result.append(("이전 기록", older)) }
        return result
    }

    var body: some View {
        NavigationStack {
            List {
                let groups = groupedHistory()
                if groups.isEmpty {
                    Text("방문 기록이 없습니다.").foregroundStyle(.secondary)
                } else {
                    ForEach(groups, id: \.title) { section in
                        Section(section.title) {
                            ForEach(section.items) { item in
                                Button {
                                    model.open(item.url)
                                    dismiss()
                                } label: {
                                    HStack {
                                        VStack(alignment: .leading, spacing: 2) {
                                            Text(item.title).font(.subheadline.weight(.semibold)).lineLimit(1)
                                            Text(item.url).font(.caption2).foregroundStyle(.secondary).lineLimit(1)
                                        }
                                        Spacer()
                                        Text(Date(timeIntervalSince1970: Double(item.visitedAtEpochMs) / 1000.0), format: .dateTime.hour().minute())
                                            .font(.caption2.monospacedDigit())
                                            .foregroundStyle(.secondary)
                                    }
                                }
                                .swipeActions {
                                    Button("삭제", role: .destructive) {
                                        model.history.removeAll { $0.id == item.id }
                                        model.persist()
                                    }
                                }
                            }
                        }
                    }
                }
            }
            .searchable(text: $search, prompt: "방문 기록 검색")
            .navigationTitle("방문 기록")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .topBarLeading) {
                    Button("전체 삭제", role: .destructive) { confirmClear = true }
                        .disabled(model.history.isEmpty)
                }
                ToolbarItem(placement: .confirmationAction) { Button("완료") { dismiss() } }
            }
            .confirmationDialog("모든 방문 기록을 삭제할까요?", isPresented: $confirmClear, titleVisibility: .visible) {
                Button("모두 삭제", role: .destructive) {
                    model.history.removeAll()
                    model.persist()
                }
                Button("취소", role: .cancel) {}
            }
        }
    }
}

// MARK: - Download Confirmation Sheet (Android DownloadConfirmationDialog.kt Parity!)

struct DownloadConfirmationSheet: View {
    let prompt: PendingDownloadPrompt
    @ObservedObject var model: BrowserModel
    @ObservedObject var shelf: ShelfModel = .shared
    @State private var fileName: String = ""
    @State private var selectedFolder: String = ""
    @State private var showNewFolder = false
    @State private var newFolderName = ""

    var body: some View {
        NavigationStack {
            Form {
                Section("파일 정보") {
                    TextField("파일 이름", text: $fileName)
                        .autocorrectionDisabled()
                    LabeledContent("유형") { Text(prompt.mimeType).font(.caption).foregroundStyle(.secondary) }
                }

                if prompt.isImage {
                    Section("책장에 바로 저장 (이미지)") {
                        Picker("대상 책장 폴더", selection: $selectedFolder) {
                            ForEach(shelf.allDisplayFolders, id: \.self) { folder in
                                Text(folder).tag(folder)
                            }
                        }
                        Button {
                            newFolderName = ""
                            showNewFolder = true
                        } label: {
                            Label("새 책장 폴더 추가", systemImage: "folder.badge.plus")
                        }
                        Button {
                            let target = selectedFolder.isEmpty ? shelf.activeFolder : selectedFolder
                            model.commitPendingDownload(fileName: fileName, saveToBookshelfFolder: target)
                        } label: {
                            Label("책장에 바로 저장", systemImage: "books.vertical.fill")
                                .frame(maxWidth: .infinity)
                        }
                        .buttonStyle(.borderedProminent)
                    }
                }

                Section {
                    Button {
                        model.commitPendingDownload(fileName: fileName, saveToBookshelfFolder: nil)
                    } label: {
                        Label("다운로드 폴더에 저장", systemImage: "arrow.down.circle.fill")
                            .frame(maxWidth: .infinity)
                    }
                    .buttonStyle(.borderedProminent)
                }
            }
            .navigationTitle("파일 다운로드")
            .navigationBarTitleDisplayMode(.inline)
            .onAppear {
                fileName = prompt.suggestedFilename
                selectedFolder = shelf.activeFolder
            }
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("취소") { model.cancelPendingDownload() }
                }
            }
            .alert("새 책장 폴더", isPresented: $showNewFolder) {
                TextField("폴더 이름", text: $newFolderName)
                Button("만들기") {
                    if let created = shelf.addFolder(newFolderName, select: false) {
                        selectedFolder = created
                    }
                }
                Button("취소", role: .cancel) {}
            }
        }
        .presentationDetents([.medium, .large])
    }
}

struct DownloadsSheet: View {
    @EnvironmentObject private var model: BrowserModel
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        NavigationStack {
            List {
                if model.downloads.isEmpty {
                    Text("다운로드한 파일이 없습니다.").font(.subheadline).foregroundStyle(.secondary)
                } else {
                    ForEach(model.downloads, id: \.self) { file in
                        HStack {
                            Image(systemName: "doc.fill").foregroundStyle(.blue)
                            Text(file.lastPathComponent).lineLimit(1)
                            Spacer()
                            ShareLink(item: file) { Image(systemName: "square.and.arrow.up") }
                        }
                        .swipeActions {
                            Button("삭제", role: .destructive) {
                                try? FileManager.default.removeItem(at: file)
                                model.refreshDownloadsList()
                            }
                        }
                    }
                }
            }
            .navigationTitle("다운로드")
            .navigationBarTitleDisplayMode(.inline)
            .onAppear { model.refreshDownloadsList() }
            .toolbar {
                ToolbarItem(placement: .confirmationAction) { Button("완료") { dismiss() } }
            }
        }
    }
}

// MARK: - Profile Manager With 18 Icons & Color Palette + ColorPicker + HEX (Android ProfileSettingsActivity Parity!)

struct ProfileManagerSheet: View {
    @EnvironmentObject private var model: BrowserModel
    @Environment(\.dismiss) private var dismiss
    @State private var editingProfile: BrowserProfile? = nil

    var body: some View {
        NavigationStack {
            List {
                Section("프로필 (최대 8개)") {
                    ForEach(model.profiles) { profile in
                        HStack(spacing: 12) {
                            Image(systemName: profile.icon)
                                .font(.title3)
                                .foregroundStyle(Color(hex: profile.colorHex))
                                .frame(width: 34, height: 34)
                                .background(Color(hex: profile.colorHex).opacity(0.16), in: Circle())
                            VStack(alignment: .leading, spacing: 2) {
                                Text(profile.name).font(.subheadline.weight(.semibold))
                                if profile.id == model.activeProfileID {
                                    Text("현재 활성 프로필").font(.caption2).foregroundStyle(.blue)
                                }
                            }
                            Spacer()
                            Button("편집") { editingProfile = profile }
                                .buttonStyle(.bordered)
                                .controlSize(.small)
                        }
                        .contentShape(Rectangle())
                        .onTapGesture {
                            model.selectProfile(profile.id)
                            dismiss()
                        }
                        .swipeActions {
                            if profile.id != BrowserModel.defaultProfileID {
                                Button("삭제", role: .destructive) { model.deleteProfile(profile.id) }
                            }
                        }
                    }
                }
            }
            .navigationTitle("프로필 관리")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .topBarLeading) {
                    Button {
                        editingProfile = BrowserProfile(name: "", icon: "person.fill", colorHex: "#0A84FF")
                    } label: {
                        Image(systemName: "plus")
                    }
                    .disabled(model.profiles.count >= 8)
                }
                ToolbarItem(placement: .confirmationAction) { Button("완료") { dismiss() } }
            }
            .sheet(item: $editingProfile) { prof in
                ProfileEditorSheet(initial: prof) { saved in
                    model.saveProfile(saved)
                }
            }
        }
    }
}

private struct ProfileEditorSheet: View {
    @Environment(\.dismiss) private var dismiss
    let initial: BrowserProfile
    let onSave: (BrowserProfile) -> Void
    @State private var name: String = ""
    @State private var icon: String = "person.fill"
    @State private var hex: String = "#0A84FF"
    @State private var pickedColor: Color = .blue

    private let iconChoices = [
        "person.fill", "briefcase.fill", "star.fill", "heart.fill", "flame.fill", "bolt.fill",
        "book.fill", "gamecontroller.fill", "graduationcap.fill", "music.note", "film.fill", "camera.fill",
        "globe", "shield.fill", "sparkles", "moon.fill", "sun.max.fill", "leaf.fill"
    ]
    private let paletteHex = ["#0A84FF", "#30D158", "#FF9F0A", "#FF453A", "#BF5AF2", "#64D2FF", "#FF375F", "#FFD60A"]

    var body: some View {
        NavigationStack {
            Form {
                Section("프로필 이름") {
                    TextField("이름 (예: 개인, 업무, 웹툰)", text: $name)
                }
                Section("아이콘 선택") {
                    LazyVGrid(columns: Array(repeating: GridItem(.flexible()), count: 6), spacing: 12) {
                        ForEach(iconChoices, id: \.self) { sym in
                            Button {
                                icon = sym
                            } label: {
                                Image(systemName: sym)
                                    .font(.title3)
                                    .foregroundStyle(Color(hex: hex))
                                    .frame(width: 40, height: 40)
                                    .background(icon == sym ? Color.blue.opacity(0.28) : Color.clear, in: RoundedRectangle(cornerRadius: 10))
                            }
                            .buttonStyle(.plain)
                        }
                    }
                    .padding(.vertical, 4)
                }
                Section("색상 선택") {
                    ScrollView(.horizontal, showsIndicators: false) {
                        HStack(spacing: 10) {
                            ForEach(paletteHex, id: \.self) { code in
                                Circle()
                                    .fill(Color(hex: code))
                                    .frame(width: 32, height: 32)
                                    .overlay(Circle().strokeBorder(.white, lineWidth: hex == code ? 2.5 : 0))
                                    .onTapGesture {
                                        hex = code
                                        pickedColor = Color(hex: code)
                                    }
                            }
                        }
                        .padding(.vertical, 4)
                    }
                    ColorPicker("사용자 지정 색상", selection: $pickedColor, supportsOpacity: false)
                        .onChange(of: pickedColor) { newColor in
                            hex = newColor.toHexString()
                        }
                    TextField("#0A84FF", text: $hex)
                        .textInputAutocapitalization(.characters)
                        .autocorrectionDisabled()
                }
            }
            .navigationTitle(initial.name.isEmpty ? "새 프로필" : "프로필 편집")
            .navigationBarTitleDisplayMode(.inline)
            .onAppear {
                name = initial.name
                icon = initial.icon
                hex = initial.colorHex
                pickedColor = Color(hex: initial.colorHex)
            }
            .toolbar {
                ToolbarItem(placement: .cancellationAction) { Button("취소") { dismiss() } }
                ToolbarItem(placement: .confirmationAction) {
                    Button("저장") {
                        let trimmed = name.trimmingCharacters(in: .whitespacesAndNewlines)
                        guard !trimmed.isEmpty else { return }
                        var updated = initial
                        updated.name = trimmed
                        updated.icon = icon
                        updated.colorHex = hex
                        onSave(updated)
                        dismiss()
                    }
                }
            }
        }
    }
}

// MARK: - Toolbar Shortcuts Editor Sheet (All 20 Android Shortcuts!)

struct ToolbarEditorSheet: View {
    @Environment(\.dismiss) private var dismiss
    @ObservedObject var model: BrowserModel

    private var availableToAdd: [ToolbarShortcutAction] {
        let active = Set(model.preferences.toolbarShortcuts)
        return ToolbarShortcutAction.allCases.filter { !active.contains($0) }
    }

    var body: some View {
        NavigationStack {
            List {
                Section("현재 도구모음 버튼 (드래그하여 순서 변경)") {
                    ForEach(model.preferences.toolbarShortcuts) { action in
                        HStack {
                            Label(action.title, systemImage: action.systemImage)
                            Spacer()
                            if model.preferences.toolbarShortcuts.count > 2 {
                                Button(role: .destructive) {
                                    model.preferences.toolbarShortcuts.removeAll { $0 == action }
                                    model.savePreferences()
                                } label: {
                                    Image(systemName: "minus.circle.fill").foregroundStyle(.red)
                                }
                                .buttonStyle(.plain)
                            }
                        }
                    }
                    .onMove { src, dst in
                        model.preferences.toolbarShortcuts.move(fromOffsets: src, toOffset: dst)
                        model.savePreferences()
                    }
                }

                Section("추가 가능한 바로가기 (\(availableToAdd.count))") {
                    ForEach(availableToAdd) { action in
                        Button {
                            model.preferences.toolbarShortcuts.append(action)
                            model.savePreferences()
                        } label: {
                            HStack {
                                Label(action.title, systemImage: action.systemImage)
                                Spacer()
                                Image(systemName: "plus.circle.fill").foregroundStyle(.blue)
                            }
                        }
                    }
                }

                Section {
                    Button("기본 도구모음 구성으로 복원") {
                        model.preferences.toolbarShortcuts = ToolbarShortcutAction.defaultOrder
                        model.savePreferences()
                    }
                }
            }
            .environment(\.editMode, .constant(.active))
            .navigationTitle("도구모음 바로가기 편집")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .confirmationAction) { Button("완료") { dismiss() } }
            }
        }
    }
}

// MARK: - Full Categorized Settings Sheet (Exact 8-Category Android SettingsActivity.kt Parity!)

struct SettingsSheet: View {
    @Environment(\.dismiss) private var dismiss
    @ObservedObject var model: BrowserModel
    var onOpenSync: () -> Void
    var onOpenExtensions: () -> Void
    var onOpenNetwork: () -> Void
    var onOpenAppLock: () -> Void
    var onOpenProfiles: () -> Void
    var onCheckUpdates: () -> Void
    @State private var showToolbarEditor = false
    @State private var confirmClearAllData = false
    @State private var confirmResetSettings = false

    var body: some View {
        NavigationStack {
            List {
                // 0. VPN & Security DNS
                Section {
                    Button(action: onOpenNetwork) {
                        Label("VPN · 보안 DNS (DoH / WireGuard)", systemImage: "network.badge.shield.half.filled")
                    }
                }

                // 1. General
                Section("일반") {
                    NavigationLink {
                        Form {
                            Section("시작 페이지 / 홈페이지") {
                                TextField("about:blank 또는 URL", text: $model.preferences.homePage)
                                    .textInputAutocapitalization(.never)
                                    .autocorrectionDisabled()
                            }
                            Section("기본 검색 엔진") {
                                Picker("검색 엔진", selection: $model.preferences.searchEngine) {
                                    ForEach(SearchEngineChoice.allCases) { eng in
                                        Text(eng.rawValue).tag(eng)
                                    }
                                }
                                if model.preferences.searchEngine == .custom {
                                    TextField("https://example.com/search?q=%s", text: $model.preferences.customSearchTemplate)
                                        .textInputAutocapitalization(.never)
                                        .autocorrectionDisabled()
                                }
                            }
                            Section("미디어 / PiP") {
                                Stepper("건너뛰기 시간: \(model.preferences.pipSeekSeconds)초", value: $model.preferences.pipSeekSeconds, in: 1...3600, step: 5)
                            }
                        }
                        .navigationTitle("일반")
                    } label: {
                        Label("일반 (검색 엔진 · 시작 페이지 · PiP)", systemImage: "slider.horizontal.3")
                    }

                    // 2. Appearance
                    NavigationLink {
                        Form {
                            Section("테마 및 시각 효과") {
                                Picker("페이지 색상 테마", selection: $model.preferences.pageColorScheme) {
                                    ForEach(PageColorSchemeChoice.allCases) { scheme in
                                        Text(scheme.rawValue).tag(scheme)
                                    }
                                }
                                Toggle("Liquid Glass UI (반투명 글래스)", isOn: $model.preferences.liquidGlassUi)
                                Toggle("컴팩트 도구모음", isOn: $model.preferences.compactToolbar)
                            }
                            Section("주소창 및 스크롤 동작") {
                                Picker("주소창 위치", selection: $model.preferences.addressBarPosition) {
                                    ForEach(AddressBarPositionChoice.allCases) { pos in
                                        Text(pos.rawValue).tag(pos)
                                    }
                                }
                                .pickerStyle(.segmented)
                                Toggle("스크롤 시 도구모음 숨기기", isOn: $model.preferences.hideToolbarOnScroll)
                                Toggle("위로 스크롤 시 도구모음 펼치기", isOn: $model.preferences.expandToolbarOnScrollUp)
                                Toggle("캡처 영역 좌우 여백 표시", isOn: $model.preferences.captureSideMargins)
                                Button("도구모음 바로가기 편집...") { showToolbarEditor = true }
                            }
                            Section("글자 및 페이지 기본 배율") {
                                Stepper("기본 배율: \(Int((model.preferences.fontScale * 100).rounded()))%",
                                        value: $model.preferences.fontScale, in: 0.5...2.0, step: 0.05)
                            }
                        }
                        .navigationTitle("화면 및 모양")
                    } label: {
                        Label("화면 및 모양 (테마 · Liquid Glass · 주소창)", systemImage: "paintbrush")
                    }

                    // 3. Tabs
                    NavigationLink {
                        Form {
                            Section("탭 목록 레이아웃") {
                                Picker("탭 썸네일 비율", selection: $model.preferences.tabThumbnailAspectRatio) {
                                    ForEach(TabAspectRatioChoice.allCases) { ratio in
                                        Text(ratio.rawValue).tag(ratio)
                                    }
                                }
                                Stepper("가로 탭 칸 수: \(model.preferences.tabGridColumns)열", value: $model.preferences.tabGridColumns, in: 1...10)
                                Toggle("아래쪽부터 탭 배치", isOn: $model.preferences.tabsStartFromBottom)
                                Toggle("탭 미리보기 썸네일 유지", isOn: $model.preferences.showTabThumbnails)
                            }
                            Section("탭 동작") {
                                Toggle("새 창 링크를 새 탭에서 열기", isOn: $model.preferences.openLinksInNewTab)
                                Toggle("새 탭 열 때 키보드 자동 표시", isOn: $model.preferences.showKeyboardOnNewTab)
                                Toggle("탭 전환 시 키보드 유지", isOn: $model.preferences.keepKeyboardAcrossTabSwitch)
                                Toggle("앱 시작 시 이전 열린 탭 복원", isOn: $model.preferences.restoreTabs)
                                Button("프로필 관리 (생성 · 아이콘 · 색상)...", action: onOpenProfiles)
                            }
                        }
                        .navigationTitle("탭 설정")
                    } label: {
                        Label("탭 (비율 · 칸 수 · 키보드 · 프로필)", systemImage: "square.on.square")
                    }
                }

                // 4. Privacy & Security
                Section("보안 · 동기화 · 고급") {
                    NavigationLink {
                        Form {
                            Section("추적 및 쿠키 보호") {
                                Picker("추적 방지 수준", selection: $model.preferences.trackingProtection) {
                                    ForEach(TrackingProtectionLevel.allCases) { lvl in
                                        Text(lvl.rawValue).tag(lvl)
                                    }
                                }
                                Picker("쿠키 정책", selection: $model.preferences.cookiePolicy) {
                                    ForEach(CookiePolicyLevel.allCases) { pol in
                                        Text(pol.rawValue).tag(pol)
                                    }
                                }
                                Toggle("Global Privacy Control (GPC) 신호 전송", isOn: $model.preferences.globalPrivacyControl)
                                Toggle("HTTPS 전용 연결 업그레이드", isOn: $model.preferences.automaticHTTPS)
                                Toggle("팝업 창 차단", isOn: $model.preferences.blockPopups)
                                Toggle("앱 종료 시 브라우징 데이터 자동 삭제", isOn: $model.preferences.clearDataOnExit)
                            }
                            Section("앱 잠금 및 화면 보호") {
                                Button("앱 잠금 설정 (PIN · 비밀번호 · Face ID · 검은 화면 보호)", action: onOpenAppLock)
                            }
                            Section("데이터 및 예외 관리") {
                                Button("사이트별 콘텐츠 차단 예외 초기화 (\(model.preferences.contentBlockingExceptions.count)곳)", role: .destructive) {
                                    model.clearContentBlockingExceptions()
                                }
                                Button("방문 기록 · 쿠키 · 캐시 · 미리보기 모두 삭제", role: .destructive) {
                                    confirmClearAllData = true
                                }
                            }
                        }
                        .navigationTitle("개인정보 보호")
                    } label: {
                        Label("개인정보 보호 (추적 차단 · 잠금 · 삭제)", systemImage: "lock.shield")
                    }

                    // 5. Sync
                    Button(action: onOpenSync) {
                        Label("동기화 (Google Drive · 암호화 파일)", systemImage: "arrow.triangle.2.circlepath.icloud")
                    }

                    // 6. Advanced
                    NavigationLink {
                        Form {
                            Section("브라우징 고급 옵션") {
                                Toggle("주소창에 전체 URL 표시", isOn: $model.preferences.showFullURL)
                                Toggle("백그라운드 오디오 재생 허용", isOn: $model.preferences.backgroundAudio)
                                Toggle("JavaScript 실행 허용", isOn: $model.preferences.javaScriptEnabled)
                                Toggle("기본적으로 데스크톱 사이트 요청", isOn: $model.preferences.desktopModeDefault)
                                Button("확장 기능 · 이미지 선로딩 · 유저스크립트", action: onOpenExtensions)
                            }
                            Section("워터마크 삭제 웹 도구 URL") {
                                TextField("예: https://www.watermarkremover.io/ko", text: $model.preferences.watermarkRemoverUrl)
                                    .textInputAutocapitalization(.never)
                                    .autocorrectionDisabled()
                                    .keyboardType(.URL)
                            }
                            Section("초기화") {
                                Button("저장된 탭 미리보기만 삭제", role: .destructive) {
                                    model.clearTabPreviews()
                                }
                                Button("모든 설정 기본값으로 초기화", role: .destructive) {
                                    confirmResetSettings = true
                                }
                            }
                        }
                        .navigationTitle("고급 설정")
                    } label: {
                        Label("고급 (확장 기능 · 워터마크 URL · 초기화)", systemImage: "gearshape.2")
                    }

                    // 7. App Info
                    NavigationLink {
                        Form {
                            Section("앱 정보") {
                                LabeledContent("앱 이름") { Text("Omyaru Browser") }
                                LabeledContent("버전") { Text("v\(BrowserModel.appVersionName) (\(BrowserModel.appBuildNumber))") }
                                LabeledContent("브라우저 엔진") { Text("Apple WebKit (WKWebView)") }
                                Button("GitHub / AltStore 업데이트 확인", action: onCheckUpdates)
                                Button("GitHub 저장소 열기") {
                                    dismiss()
                                    model.open("https://github.com/omyeol/Omyaru-Browser")
                                }
                            }
                        }
                        .navigationTitle("정보")
                    } label: {
                        Label("정보 (v\(BrowserModel.appVersionName) · 업데이트)", systemImage: "info.circle")
                    }
                }
            }
            .onChange(of: model.preferences) { _ in
                model.savePreferences()
                model.applyUserScriptsToAllWebViews()
            }
            .navigationTitle("설정")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .confirmationAction) { Button("완료") { dismiss() } }
            }
            .sheet(isPresented: $showToolbarEditor) {
                ToolbarEditorSheet(model: model)
            }
            .confirmationDialog("방문 기록·쿠키·캐시·미리보기를 모두 삭제할까요?", isPresented: $confirmClearAllData, titleVisibility: .visible) {
                Button("모두 삭제", role: .destructive) { Task { await model.clearBrowsingData() } }
                Button("취소", role: .cancel) {}
            }
            .confirmationDialog("모든 설정을 기본값으로 초기화할까요?", isPresented: $confirmResetSettings, titleVisibility: .visible) {
                Button("초기화", role: .destructive) { model.resetAllPreferences() }
                Button("취소", role: .cancel) {}
            }
        }
    }
}

// MARK: - Color Hex Helpers

extension Color {
    init(hex: String) {
        let cleaned = hex.trimmingCharacters(in: CharacterSet.alphanumerics.inverted)
        var int: UInt64 = 0
        Scanner(string: cleaned).scanHexInt64(&int)
        let r, g, b: Double
        if cleaned.count == 6 {
            r = Double((int >> 16) & 0xFF) / 255.0
            g = Double((int >> 8) & 0xFF) / 255.0
            b = Double(int & 0xFF) / 255.0
        } else {
            r = 0.04; g = 0.52; b = 1.0
        }
        self.init(red: r, green: g, blue: b)
    }

    func toHexString() -> String {
        let ui = UIColor(self)
        var r: CGFloat = 0, g: CGFloat = 0, b: CGFloat = 0, a: CGFloat = 0
        ui.getRed(&r, green: &g, blue: &b, alpha: &a)
        return String(format: "#%02X%02X%02X", Int(r * 255), Int(g * 255), Int(b * 255))
    }
}
