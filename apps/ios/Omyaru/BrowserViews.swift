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
    @State private var showHistory = false
    @State private var showDownloads = false
    @State private var showBookshelf = false
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
    @State private var showFindBar = false
    @State private var findInput = ""
    @State private var showAddFavorite = false
    @State private var newFavTitle = ""
    @State private var newFavURL = ""
    @State private var updateMessage: String? = nil
    @FocusState private var addressFocused: Bool

    var body: some View {
        ZStack {
            Color(red: 0.07, green: 0.07, blue: 0.08).ignoresSafeArea()

            if let tab = model.activeTab {
                if model.isStartPage {
                    startPageView
                } else {
                    BrowserWebView(webView: model.webView(for: tab.id, loadIfNeeded: true))
                        .id(tab.id)
                }
            }

            if showFindBar && !model.isStartPage {
                VStack {
                    findInPageBar
                    Spacer()
                }
            }

            // Floating compact pill to restore collapsed toolbar
            if model.isChromeCollapsed && !model.isStartPage {
                VStack {
                    if model.preferences.addressBarPosition == .bottom { Spacer() }
                    Button {
                        withAnimation(.spring(response: 0.25, dampingFraction: 0.85)) {
                            model.isChromeCollapsed = false
                        }
                    } label: {
                        HStack(spacing: 6) {
                            Image(systemName: "lock.fill").font(.caption2)
                            Text(URL(string: model.currentURL)?.host ?? model.currentURL)
                                .font(.caption.weight(.semibold))
                                .lineLimit(1)
                        }
                        .padding(.horizontal, 14)
                        .padding(.vertical, 6)
                        .background(.ultraThinMaterial, in: Capsule())
                        .shadow(radius: 6)
                    }
                    .buttonStyle(.plain)
                    .padding(.vertical, 6)
                    if model.preferences.addressBarPosition == .top { Spacer() }
                }
            }
        }
        .safeAreaInset(edge: .top) {
            if model.preferences.addressBarPosition == .top && !model.isChromeCollapsed {
                browserChrome
            }
        }
        .safeAreaInset(edge: .bottom) {
            if model.preferences.addressBarPosition == .bottom && !model.isChromeCollapsed {
                browserChrome
            }
        }
        .sheet(isPresented: $showTabs) {
            TabOverview().environmentObject(model)
        }
        .sheet(isPresented: $showBookmarks) {
            BookmarksSheet().environmentObject(model)
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
        .sheet(isPresented: $showPageTools) {
            PageToolsSheet(
                model: model,
                onStartFind: {
                    showPageTools = false
                    showFindBar = true
                },
                onOpenCapture: {
                    showPageTools = false
                    DispatchQueue.main.asyncAfter(deadline: .now() + 0.25) { showLiveCapture = true }
                },
                onOpenWatermark: {
                    showPageTools = false
                    DispatchQueue.main.asyncAfter(deadline: .now() + 0.25) { showWatermarkRemover = true }
                },
                onOpenExtensions: {
                    showPageTools = false
                    DispatchQueue.main.asyncAfter(deadline: .now() + 0.25) { showExtensions = true }
                }
            )
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
            address = URL(string: raw)?.host ?? raw
        }
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

    // MARK: - Top or Bottom Safari Smart Search Bar & Full Customizable Toolbar

    private var browserChrome: some View {
        VStack(spacing: 6) {
            if addressFocused && !address.isEmpty {
                let suggestions = model.urlSuggestions(for: address)
                if !suggestions.isEmpty {
                    ScrollView(.horizontal, showsIndicators: false) {
                        HStack(spacing: 8) {
                            ForEach(suggestions) { item in
                                Button {
                                    address = item.url
                                    model.open(item.url)
                                    addressFocused = false
                                } label: {
                                    HStack(spacing: 4) {
                                        Image(systemName: "globe").font(.caption2).foregroundStyle(.blue)
                                        Text(item.title).font(.caption.weight(.medium)).lineLimit(1)
                                    }
                                    .padding(.horizontal, 10)
                                    .padding(.vertical, 6)
                                    .background(chromeSurfaceColor, in: Capsule())
                                }
                                .buttonStyle(.plain)
                            }
                        }
                        .padding(.horizontal, 4)
                    }
                }
            }

            if model.isLoading {
                ProgressView(value: model.estimatedProgress)
                    .tint(.blue)
                    .scaleEffect(x: 1, y: 0.6)
            }

            // Address Capsule
            HStack(spacing: 8) {
                Button {
                    showPageTools = true
                } label: {
                    Image(systemName: "puzzlepiece.extension")
                        .font(.subheadline)
                        .foregroundStyle(model.isContentBlockingDisabledForCurrentHost ? .orange : .blue)
                }

                Image(systemName: model.currentURL.lowercased().hasPrefix("https") ? "lock.fill" : "magnifyingglass")
                    .font(.caption)
                    .foregroundStyle(.secondary)

                TextField("검색 또는 웹 사이트 이름 입력", text: $address)
                    .textInputAutocapitalization(.never)
                    .autocorrectionDisabled()
                    .keyboardType(.URL)
                    .submitLabel(.go)
                    .focused($addressFocused)
                    .onSubmit {
                        model.open(address)
                        addressFocused = false
                    }

                if addressFocused && !address.isEmpty {
                    Button { address = "" } label: {
                        Image(systemName: "xmark.circle.fill").foregroundStyle(.secondary)
                    }
                } else if !model.isStartPage {
                    Button { model.toggleBookmark() } label: {
                        Image(systemName: model.isCurrentBookmarked ? "star.fill" : "star")
                            .foregroundStyle(model.isCurrentBookmarked ? .yellow : .secondary)
                    }
                    Button { model.reload() } label: {
                        Image(systemName: "arrow.clockwise")
                            .font(.subheadline)
                            .foregroundStyle(.secondary)
                    }
                }
            }
            .padding(.horizontal, 12)
            .padding(.vertical, model.preferences.compactToolbar ? 7 : 10)
            .background(
                model.preferences.liquidGlassUi ? Color.white.opacity(0.11) : Color(red: 0.14, green: 0.14, blue: 0.16),
                in: RoundedRectangle(cornerRadius: 14, style: .continuous)
            )
            .overlay(
                RoundedRectangle(cornerRadius: 14, style: .continuous)
                    .strokeBorder(addressFocused ? Color.blue : Color.white.opacity(0.12), lineWidth: 1)
            )

            // Customizable Toolbar Row (Supports ALL 20 Android ToolbarShortcuts!)
            let activeShortcuts = model.preferences.toolbarShortcuts.isEmpty
                ? ToolbarShortcutAction.defaultOrder
                : model.preferences.toolbarShortcuts
            HStack {
                ForEach(Array(activeShortcuts.enumerated()), id: \.offset) { idx, shortcut in
                    if idx > 0 { Spacer(minLength: 4) }
                    toolbarItemView(for: shortcut)
                }
            }
            .font(.system(size: 19))
            .padding(.horizontal, 8)
            .padding(.top, 2)

            if !model.status.isEmpty {
                Text(model.status)
                    .font(.caption2)
                    .lineLimit(1)
                    .foregroundStyle(.secondary)
            }
        }
        .buttonStyle(.plain)
        .padding(.horizontal, 12)
        .padding(.top, 8)
        .padding(.bottom, 4)
        .background {
            if model.preferences.liquidGlassUi {
                Rectangle().fill(.ultraThinMaterial)
            } else {
                Color(red: 0.11, green: 0.11, blue: 0.12).opacity(0.96)
            }
        }
    }

    @ViewBuilder
    private func toolbarItemView(for shortcut: ToolbarShortcutAction) -> some View {
        if shortcut == .menu {
            Menu {
                Button { model.newTab() } label: { Label("새 탭", systemImage: "plus") }
                Button {
                    if let active = model.activeTab { model.duplicateTab(active.id) }
                } label: {
                    Label("현재 탭 복제 (바로 뒤에 생성)", systemImage: "plus.square.on.square")
                }
                Button { model.restoreClosedTab() } label: { Label("닫은 탭 복원", systemImage: "arrow.uturn.backward") }
                Divider()
                Button { showLiveCapture = true } label: { Label("실시간 영역 캡처", systemImage: "viewfinder") }
                Button { showWatermarkRemover = true } label: { Label("워터마크 제거", systemImage: "wand.and.stars") }
                Button { showBookshelf = true } label: { Label("책장", systemImage: "books.vertical") }
                Divider()
                Button { model.toggleBookmark() } label: { Label(model.isCurrentBookmarked ? "북마크 해제" : "북마크에 추가", systemImage: "star") }
                Button { showBookmarks = true } label: { Label("북마크", systemImage: "book") }
                Button { showHistory = true } label: { Label("방문 기록", systemImage: "clock") }
                Button { showDownloads = true } label: { Label("다운로드", systemImage: "arrow.down.circle") }
                Divider()
                Button { showPageTools = true } label: { Label("페이지 도구 (배율·차단·찾기)", systemImage: "slider.horizontal.3") }
                Button { showExtensions = true } label: { Label("확장 기능 · 유저스크립트", systemImage: "puzzlepiece.extension") }
                Button { showNetworkPrivacy = true } label: { Label("VPN · 보안 DNS", systemImage: "network.badge.shield.half.filled") }
                Button { showDriveSync = true } label: { Label("동기화", systemImage: "arrow.triangle.2.circlepath.icloud") }
                Button { showSettings = true } label: { Label("설정", systemImage: "gearshape") }
                Button { Task { await checkUpdates() } } label: { Label("업데이트 확인", systemImage: "arrow.triangle.2.circlepath") }
            } label: {
                Image(systemName: "ellipsis.circle")
            }
        } else {
            Button {
                triggerToolbarShortcut(shortcut)
            } label: {
                if shortcut == .tabs {
                    ZStack {
                        Image(systemName: "square.on.square")
                        Text("\(model.visibleTabs.count)")
                            .font(.system(size: 9, weight: .bold))
                            .offset(x: 1, y: 1)
                    }
                } else {
                    Image(systemName: shortcut.systemImage)
                }
            }
            .disabled((shortcut == .back && !model.canGoBack) || (shortcut == .forward && !model.canGoForward))
        }
    }

    private func triggerToolbarShortcut(_ action: ToolbarShortcutAction) {
        switch action {
        case .back: model.back()
        case .forward: model.forward()
        case .home: model.home()
        case .newTab: _ = model.newTab()
        case .tabs: showTabs = true
        case .menu: showSettings = true
        case .findInPage: showFindBar = true
        case .contentBlocking: model.toggleContentBlockingForCurrentHost()
        case .desktopSite: model.toggleDesktopMode()
        case .extensions: showExtensions = true
        case .capture: showLiveCapture = true
        case .watermark: showWatermarkRemover = true
        case .bookshelf: showBookshelf = true
        case .vpn: showNetworkPrivacy = true
        case .history: showHistory = true
        case .bookmarks: showBookmarks = true
        case .share, .copyAddress:
            UIPasteboard.general.string = model.currentURL
            model.status = "현재 주소를 클립보드에 복사했습니다."
        case .reopenClosedTab: model.restoreClosedTab()
        case .closeAllTabs: model.closeAllVisibleTabs()
        case .settings: showSettings = true
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

// MARK: - Tab Overview (Respects Grid Columns 1..10, Aspect Ratio 1:1/3:4/16:9, Tabs From Bottom, Drag Auto-Scroll, Duplicate After Source!)

struct TabOverview: View {
    @EnvironmentObject private var model: BrowserModel
    @Environment(\.dismiss) private var dismiss
    @State private var searchQuery = ""
    @State private var useGridLayout = true
    @State private var newGroupName = ""
    @State private var showingNewGroup = false
    @State private var draggingTabID: UUID? = nil

    private var filteredTabs: [BrowserTab] {
        let q = searchQuery.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        let base = q.isEmpty ? model.visibleTabs : model.visibleTabs.filter {
            $0.title.lowercased().contains(q) || $0.url.lowercased().contains(q)
        }
        return model.preferences.tabsStartFromBottom ? Array(base.reversed()) : base
    }

    var body: some View {
        NavigationStack {
            VStack(spacing: 0) {
                groupAndSearchHeader
                if useGridLayout { gridTabBody } else { listTabBody }
                bottomOverviewBar
            }
            .background(Color(red: 0.07, green: 0.07, blue: 0.08))
            .navigationTitle("탭 (\(model.visibleTabs.count))")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .topBarLeading) {
                    Button { useGridLayout.toggle() } label: {
                        Image(systemName: useGridLayout ? "list.bullet" : "square.grid.2x2")
                    }
                }
                ToolbarItemGroup(placement: .topBarTrailing) {
                    Button {
                        withAnimation(.spring(response: 0.28, dampingFraction: 0.82)) {
                            _ = model.newTab()
                        }
                        dismiss()
                    } label: {
                        Image(systemName: "plus")
                    }
                    Button("완료") { dismiss() }.fontWeight(.semibold)
                }
            }
            .alert("새 탭 그룹", isPresented: $showingNewGroup) {
                TextField("그룹 이름", text: $newGroupName)
                Button("만들기") { model.addGroup(newGroupName); newGroupName = "" }
                Button("취소", role: .cancel) { newGroupName = "" }
            }
        }
    }

    private var groupAndSearchHeader: some View {
        VStack(spacing: 8) {
            ScrollView(.horizontal, showsIndicators: false) {
                HStack(spacing: 8) {
                    Menu {
                        ForEach(model.profiles) { profile in
                            Button { model.selectProfile(profile.id) } label: {
                                Label(profile.name, systemImage: profile.icon)
                            }
                        }
                    } label: {
                        HStack(spacing: 4) {
                            Image(systemName: model.activeProfile.icon)
                                .foregroundStyle(Color(hex: model.activeProfile.colorHex))
                            Text(model.activeProfile.name)
                            Image(systemName: "chevron.down").font(.caption2)
                        }
                        .font(.caption.weight(.bold))
                        .padding(.horizontal, 11)
                        .padding(.vertical, 7)
                        .background(Color(red: 0.19, green: 0.19, blue: 0.22), in: Capsule())
                    }

                    groupChip(title: "비공개", id: BrowserModel.privateGroupID, icon: "hand.raised.fill")
                    ForEach(model.profileGroups) { group in
                        groupChip(title: group.name, id: group.id, icon: "folder")
                    }
                    Button { showingNewGroup = true } label: {
                        Label("그룹", systemImage: "plus")
                            .font(.caption.weight(.semibold))
                            .padding(.horizontal, 10)
                            .padding(.vertical, 7)
                            .background(Color(red: 0.16, green: 0.16, blue: 0.18), in: Capsule())
                    }
                    .buttonStyle(.plain)
                }
                .padding(.horizontal, 12)
            }

            HStack(spacing: 8) {
                Image(systemName: "magnifyingglass").foregroundStyle(.secondary)
                TextField("탭 제목 또는 주소 검색", text: $searchQuery).font(.subheadline)
                if !searchQuery.isEmpty {
                    Button { searchQuery = "" } label: {
                        Image(systemName: "xmark.circle.fill").foregroundStyle(.secondary)
                    }
                }
            }
            .padding(.horizontal, 10)
            .padding(.vertical, 7)
            .background(Color(red: 0.14, green: 0.14, blue: 0.16), in: RoundedRectangle(cornerRadius: 10))
            .padding(.horizontal, 12)
        }
        .padding(.vertical, 8)
        .background(Color(red: 0.10, green: 0.10, blue: 0.12))
    }

    private func groupChip(title: String, id: UUID, icon: String) -> some View {
        let active = model.activeGroupID == id
        return Button {
            withAnimation(.easeInOut(duration: 0.18)) { model.selectGroup(id) }
        } label: {
            HStack(spacing: 4) {
                Image(systemName: icon).font(.caption2)
                Text(title).font(.caption.weight(.semibold))
            }
            .foregroundStyle(active ? .white : .primary)
            .padding(.horizontal, 12)
            .padding(.vertical, 7)
            .background(active ? Color.blue : Color(red: 0.16, green: 0.16, blue: 0.18), in: Capsule())
        }
        .buttonStyle(.plain)
    }

    private var gridTabBody: some View {
        let colCount = min(10, max(1, model.preferences.tabGridColumns))
        let ratio = model.preferences.tabThumbnailAspectRatio.heightRatio
        return GeometryReader { outerGeo in
            let cellWidth = max(44, (outerGeo.size.width - CGFloat(colCount + 1) * 10) / CGFloat(colCount))
            let thumbHeight = min(280, max(64, cellWidth * ratio))
            ScrollViewReader { proxy in
                ScrollView {
                    LazyVGrid(columns: Array(repeating: GridItem(.flexible(), spacing: 10), count: colCount), spacing: 10) {
                        ForEach(filteredTabs) { tab in
                            tabGridCard(tab: tab, thumbHeight: thumbHeight, outerGeo: outerGeo, scrollProxy: proxy)
                                .id(tab.id)
                                .transition(.asymmetric(
                                    insertion: .scale(scale: 0.92).combined(with: .opacity),
                                    removal: .scale(scale: 0.78).combined(with: .opacity)
                                ))
                        }
                    }
                    .padding(10)
                    .animation(.spring(response: 0.28, dampingFraction: 0.82), value: model.tabs.map(\.id))
                }
            }
        }
    }

    private func tabGridCard(tab: BrowserTab, thumbHeight: CGFloat, outerGeo: GeometryProxy, scrollProxy: ScrollViewProxy) -> some View {
        let isActive = tab.id == model.activeID
        return VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 4) {
                Circle()
                    .fill(isActive ? Color.blue : Color.secondary.opacity(0.5))
                    .frame(width: 7, height: 7)
                Text(tab.title).font(.caption2.weight(.semibold)).lineLimit(1)
                Spacer(minLength: 2)
                Button {
                    withAnimation(.spring(response: 0.28, dampingFraction: 0.82)) {
                        model.close(tab.id)
                    }
                } label: {
                    Image(systemName: "xmark.circle.fill")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
                .buttonStyle(.plain)
            }
            .padding(.horizontal, 8)
            .padding(.vertical, 6)
            .background(Color(red: 0.16, green: 0.16, blue: 0.19))

            ZStack {
                Color(red: 0.12, green: 0.12, blue: 0.14)
                if let snap = model.tabSnapshots[tab.id] {
                    Image(uiImage: snap)
                        .resizable()
                        .scaledToFill()
                        .frame(height: thumbHeight)
                        .clipped()
                } else {
                    VStack(spacing: 4) {
                        Image(systemName: tab.url == "about:blank" ? "sparkles" : "globe")
                            .font(.title3)
                            .foregroundStyle(.secondary)
                        Text(URL(string: tab.url)?.host ?? tab.title)
                            .font(.caption2)
                            .foregroundStyle(.secondary)
                            .lineLimit(1)
                            .padding(.horizontal, 6)
                    }
                    .frame(height: thumbHeight)
                }
            }
        }
        .clipShape(RoundedRectangle(cornerRadius: 12, style: .continuous))
        .overlay(
            RoundedRectangle(cornerRadius: 12, style: .continuous)
                .strokeBorder(isActive ? Color.blue : Color.white.opacity(0.10), lineWidth: isActive ? 2 : 1)
        )
        .onTapGesture {
            model.select(tab.id)
            dismiss()
        }
        .contextMenu {
            Button {
                withAnimation(.spring(response: 0.28, dampingFraction: 0.82)) {
                    model.duplicateTab(tab.id)
                }
            } label: {
                Label("탭 복제 (바로 뒤에 생성)", systemImage: "plus.square.on.square")
            }
            Button { UIPasteboard.general.string = tab.url } label: { Label("링크 복사", systemImage: "doc.on.doc") }
            Divider()
            Button(role: .destructive) {
                withAnimation(.spring(response: 0.28, dampingFraction: 0.82)) { model.close(tab.id) }
            } label: { Label("이 탭 닫기", systemImage: "xmark") }
            Button(role: .destructive) {
                withAnimation(.spring(response: 0.28, dampingFraction: 0.82)) { model.closeOtherTabs(keeping: tab.id) }
            } label: { Label("다른 탭 모두 닫기", systemImage: "xmark.circle") }
        }
        .simultaneousGesture(
            LongPressGesture(minimumDuration: 0.35)
                .sequenced(before: DragGesture(coordinateSpace: .global))
                .onChanged { value in
                    if case let .second(true, drag?) = value {
                        draggingTabID = tab.id
                        let y = drag.location.y
                        let listIds = filteredTabs.map(\.id)
                        guard let currentIdx = listIds.firstIndex(of: tab.id) else { return }
                        if y < 170 && currentIdx > 0 {
                            let prevID = listIds[max(0, currentIdx - 1)]
                            withAnimation(.easeInOut(duration: 0.18)) {
                                model.moveVisibleTab(fromID: tab.id, toID: prevID)
                                scrollProxy.scrollTo(prevID, anchor: .top)
                            }
                        } else if y > outerGeo.size.height - 90 && currentIdx < listIds.count - 1 {
                            let nextID = listIds[min(listIds.count - 1, currentIdx + 1)]
                            withAnimation(.easeInOut(duration: 0.18)) {
                                model.moveVisibleTab(fromID: tab.id, toID: nextID)
                                scrollProxy.scrollTo(nextID, anchor: .bottom)
                            }
                        }
                    }
                }
                .onEnded { _ in draggingTabID = nil }
        )
    }

    private var listTabBody: some View {
        List {
            ForEach(filteredTabs) { tab in
                HStack(spacing: 10) {
                    Circle()
                        .fill(tab.id == model.activeID ? Color.blue : Color.secondary.opacity(0.4))
                        .frame(width: 8, height: 8)
                    VStack(alignment: .leading, spacing: 2) {
                        Text(tab.title).font(.subheadline.weight(.semibold)).lineLimit(1)
                        Text(tab.url).font(.caption2).foregroundStyle(.secondary).lineLimit(1)
                    }
                    Spacer()
                    Button {
                        withAnimation(.spring(response: 0.28, dampingFraction: 0.82)) { model.duplicateTab(tab.id) }
                    } label: { Image(systemName: "plus.square.on.square").foregroundStyle(.secondary) }
                    .buttonStyle(.plain)
                    Button {
                        withAnimation(.spring(response: 0.28, dampingFraction: 0.82)) { model.close(tab.id) }
                    } label: { Image(systemName: "xmark.circle.fill").foregroundStyle(.secondary) }
                    .buttonStyle(.plain)
                }
                .contentShape(Rectangle())
                .onTapGesture { model.select(tab.id); dismiss() }
            }
            .onMove(perform: model.reorder)
        }
        .listStyle(.plain)
        .environment(\.editMode, .constant(.active))
    }

    private var bottomOverviewBar: some View {
        HStack {
            Button {
                withAnimation(.spring(response: 0.28, dampingFraction: 0.82)) { model.restoreClosedTab() }
            } label: {
                Label("닫은 탭 복원 (\(model.closedTabs.count))", systemImage: "arrow.uturn.backward")
                    .font(.caption.weight(.semibold))
            }
            .disabled(model.closedTabs.isEmpty)
            Spacer()
            Button(role: .destructive) {
                withAnimation(.spring(response: 0.28, dampingFraction: 0.82)) { model.closeAllVisibleTabs() }
            } label: {
                Label("모두 닫기", systemImage: "trash").font(.caption.weight(.semibold))
            }
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 12)
        .background(Color(red: 0.11, green: 0.11, blue: 0.13))
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
