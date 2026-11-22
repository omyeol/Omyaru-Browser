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
        }
        .safeAreaInset(edge: .bottom) {
            bottomChrome
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
            LiveCaptureView(webView: model.activeWebView(), sourceURL: model.currentURL)
        }
        .sheet(isPresented: $showWatermarkRemover) {
            WatermarkRemoverView(webView: model.activeWebView(), sourceURL: model.currentURL)
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
                onOpenAppLock: { showSettings = false; DispatchQueue.main.asyncAfter(deadline: .now() + 0.25) { showAppLock = true } }
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
        .preferredColorScheme(.dark)
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
            VStack(alignment: .leading, spacing: 24) {
                // Header with Profile Badge & Private Mode Indicator
                HStack {
                    Button {
                        showProfiles = true
                    } label: {
                        HStack(spacing: 6) {
                            Circle()
                                .fill(Color.blue)
                                .frame(width: 8, height: 8)
                            Text(model.activeGroupID == BrowserModel.privateGroupID ? "비공개 브라우징" : model.activeProfile.name)
                                .font(.subheadline.weight(.semibold))
                            Image(systemName: "chevron.down")
                                .font(.caption2)
                        }
                        .padding(.horizontal, 12)
                        .padding(.vertical, 7)
                        .background(Color(red: 0.16, green: 0.16, blue: 0.18), in: Capsule())
                    }
                    .buttonStyle(.plain)

                    Spacer()

                    Button {
                        showDriveSync = true
                    } label: {
                        Image(systemName: "arrow.triangle.2.circlepath.icloud")
                            .padding(8)
                            .background(Color(red: 0.16, green: 0.16, blue: 0.18), in: Circle())
                    }
                    .buttonStyle(.plain)

                    Button {
                        showSettings = true
                    } label: {
                        Image(systemName: "gearshape")
                            .padding(8)
                            .background(Color(red: 0.16, green: 0.16, blue: 0.18), in: Circle())
                    }
                    .buttonStyle(.plain)
                }

                // 1. Favorites Section
                VStack(alignment: .leading, spacing: 12) {
                    Text("즐겨찾기")
                        .font(.headline.weight(.bold))
                    LazyVGrid(columns: Array(repeating: GridItem(.flexible(), spacing: 12), count: 4), spacing: 14) {
                        ForEach(model.bookmarks.prefix(11)) { bm in
                            Button {
                                model.open(bm.url)
                            } label: {
                                VStack(spacing: 6) {
                                    ZStack {
                                        RoundedRectangle(cornerRadius: 16, style: .continuous)
                                            .fill(Color(red: 0.15, green: 0.15, blue: 0.17))
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
                                    .fill(Color(red: 0.15, green: 0.15, blue: 0.17))
                                    .frame(width: 58, height: 58)
                                    .overlay {
                                        Image(systemName: "plus")
                                            .font(.title3.weight(.semibold))
                                            .foregroundStyle(.blue)
                                    }
                                Text("추가")
                                    .font(.caption)
                                    .foregroundStyle(.secondary)
                            }
                        }
                        .buttonStyle(.plain)
                    }
                }

                // 2. Privacy Report Card
                VStack(alignment: .leading, spacing: 10) {
                    Text("개인정보 보호 리포트")
                        .font(.headline.weight(.bold))
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
                            Text("분석·광고 SDK 및 서드파티 추적기를 원천 차단합니다.")
                                .font(.xsOrCaption)
                            Text("차단된 추적 요청 \(model.blockedTrackerCount)건 · \(model.tabs.count)개 탭 · \(model.profiles.count)개 프로필 로컬 보호 중")
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
                                .background(Color(red: 0.20, green: 0.20, blue: 0.23), in: Capsule())
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
                .background(Color(red: 0.13, green: 0.13, blue: 0.15), in: RoundedRectangle(cornerRadius: 18))

                // 3. Quick Tools & Library
                VStack(alignment: .leading, spacing: 10) {
                    Text("빠른 도구 및 라이브러리")
                        .font(.headline.weight(.bold))

                    LazyVGrid(columns: [GridItem(.flexible()), GridItem(.flexible())], spacing: 10) {
                        quickToolCard(
                            icon: "books.vertical.fill",
                            title: "책장 라이브러리",
                            subtitle: "폴더 트리·뷰어·PDF 내보내기"
                        ) { showBookshelf = true }

                        quickToolCard(
                            icon: "viewfinder",
                            title: "실시간 영역 캡처",
                            subtitle: "긴 웹페이지 스티칭 및 책장 저장"
                        ) { showLiveCapture = true }

                        quickToolCard(
                            icon: "wand.and.stars",
                            title: "워터마크 제거",
                            subtitle: "코너·영역 자동 복원 및 책장 연동"
                        ) { showWatermarkRemover = true }

                        quickToolCard(
                            icon: "arrow.triangle.2.circlepath.icloud.fill",
                            title: "Android · PC · iOS 동기화",
                            subtitle: "종단간 암호화 및 Google Drive 연동"
                        ) { showDriveSync = true }

                        quickToolCard(
                            icon: "puzzlepiece.extension.fill",
                            title: "확장 기능 · 유저스크립트",
                            subtitle: "이미지 선로딩·광고 차단·.user.js"
                        ) { showExtensions = true }

                        quickToolCard(
                            icon: "network.badge.shield.half.filled",
                            title: "DNS · 프록시 · 잠금",
                            subtitle: "1.1.1.1/8.8.8.8 DoH 및 Face ID"
                        ) { showNetworkPrivacy = true }
                    }
                }
            }
            .padding(18)
        }
    }

    private func quickToolCard(icon: String, title: String, subtitle: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            HStack(spacing: 10) {
                Image(systemName: icon)
                    .font(.title3)
                    .foregroundStyle(.blue)
                    .frame(width: 28)
                VStack(alignment: .leading, spacing: 2) {
                    Text(title)
                        .font(.caption.weight(.bold))
                        .foregroundStyle(.primary)
                        .lineLimit(1)
                    Text(subtitle)
                        .font(.caption2)
                        .foregroundStyle(.secondary)
                        .lineLimit(2)
                }
                Spacer(minLength: 0)
            }
            .padding(12)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(Color(red: 0.14, green: 0.14, blue: 0.16), in: RoundedRectangle(cornerRadius: 14))
        }
        .buttonStyle(.plain)
    }

    private func tileColor(for title: String) -> Color {
        let palette: [Color] = [.blue, .green, .indigo, .orange, .pink, .purple, .teal]
        let hash = abs(title.hashValue)
        return palette[hash % palette.count]
    }

    // MARK: - Find in Page Bar

    private var findInPageBar: some View {
        HStack(spacing: 8) {
            Image(systemName: "magnifyingglass").foregroundStyle(.secondary)
            TextField("페이지 내 검색...", text: $findInput)
                .textInputAutocapitalization(.never)
                .autocorrectionDisabled()
                .submitLabel(.search)
                .onSubmit {
                    Task { await model.findInPage(findInput, forward: true) }
                }
                .onChange(of: findInput) { val in
                    Task { await model.findInPage(val, forward: true) }
                }
            if model.findMatchCount > 0 {
                Text("\(model.findCurrentIndex)/\(model.findMatchCount)")
                    .font(.caption.monospacedDigit())
                    .foregroundStyle(.secondary)
            }
            Button {
                Task { await model.findInPage(findInput, forward: false) }
            } label: {
                Image(systemName: "chevron.up")
            }
            Button {
                Task { await model.findInPage(findInput, forward: true) }
            } label: {
                Image(systemName: "chevron.down")
            }
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

    // MARK: - Bottom Safari Smart Search Bar & Customizable Toolbar

    private var bottomChrome: some View {
        VStack(spacing: 6) {
            // Autocomplete Suggestions when typing
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
                                    .background(Color(red: 0.17, green: 0.17, blue: 0.20), in: Capsule())
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

            // Safari Smart Search Field Pill
            HStack(spacing: 8) {
                Button {
                    showPageTools = true
                } label: {
                    Image(systemName: "puzzlepiece.extension")
                        .font(.subheadline)
                        .foregroundStyle(.blue)
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
                    Button {
                        address = ""
                    } label: {
                        Image(systemName: "xmark.circle.fill")
                            .foregroundStyle(.secondary)
                    }
                } else if !model.isStartPage {
                    Button {
                        model.toggleBookmark()
                    } label: {
                        Image(systemName: model.isCurrentBookmarked ? "star.fill" : "star")
                            .foregroundStyle(model.isCurrentBookmarked ? .yellow : .secondary)
                    }
                    Button {
                        model.reload()
                    } label: {
                        Image(systemName: "arrow.clockwise")
                            .font(.subheadline)
                            .foregroundStyle(.secondary)
                    }
                }
            }
            .padding(.horizontal, 12)
            .padding(.vertical, model.preferences.compactToolbar ? 8 : 10)
            .background(Color(red: 0.14, green: 0.14, blue: 0.16), in: RoundedRectangle(cornerRadius: 14, style: .continuous))
            .overlay(
                RoundedRectangle(cornerRadius: 14, style: .continuous)
                    .strokeBorder(addressFocused ? Color.blue : Color.white.opacity(0.10), lineWidth: 1)
            )

            // Bottom Navigation & Shortcuts Row
            HStack {
                Button(action: model.back) {
                    Image(systemName: "chevron.left")
                }
                .disabled(!model.canGoBack)

                Spacer()

                Button(action: model.forward) {
                    Image(systemName: "chevron.right")
                }
                .disabled(!model.canGoForward)

                Spacer()

                Button(action: model.home) {
                    Image(systemName: "house")
                }

                ForEach(model.preferences.toolbarShortcuts.prefix(3)) { shortcut in
                    Spacer()
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
                }

                Spacer()

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
                    Button { showFindBar = true } label: { Label("페이지 내 찾기", systemImage: "magnifyingglass") }
                    Button { model.toggleDesktopMode() } label: {
                        Label(model.activeTab?.isDesktopMode == true ? "모바일 사이트 보기" : "데스크톱 사이트 요청", systemImage: "desktopcomputer")
                    }
                    Button { showExtensions = true } label: { Label("확장 기능 · 유저스크립트", systemImage: "puzzlepiece.extension") }
                    Button { showDriveSync = true } label: { Label("동기화", systemImage: "arrow.triangle.2.circlepath.icloud") }
                    Button { showSettings = true } label: { Label("설정", systemImage: "gearshape") }
                    Button { Task { await checkUpdates() } } label: { Label("업데이트 확인", systemImage: "arrow.triangle.2.circlepath") }
                } label: {
                    Image(systemName: "ellipsis.circle")
                }
            }
            .font(.system(size: 19))
            .padding(.horizontal, 10)
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
        .background(Color(red: 0.11, green: 0.11, blue: 0.12).opacity(0.96))
    }

    private func triggerToolbarShortcut(_ action: ToolbarShortcutAction) {
        switch action {
        case .newTab: model.newTab()
        case .tabs: showTabs = true
        case .capture: showLiveCapture = true
        case .watermark: showWatermarkRemover = true
        case .bookshelf: showBookshelf = true
        case .bookmarks: showBookmarks = true
        case .downloads: showDownloads = true
        case .sync: showDriveSync = true
        case .share:
            UIPasteboard.general.string = model.currentURL
            model.status = "현재 주소를 클립보드에 복사했습니다."
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
            let installed = Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "0.2.0"
            updateMessage = release.contains(installed)
                ? "최신 버전(\(installed))입니다."
                : "새 버전: \(release). AltStore 소스에서 업데이트할 수 있습니다."
        } catch {
            updateMessage = "업데이트를 확인하지 못했습니다: \(error.localizedDescription)"
        }
    }
}

private extension Font {
    static var xsOrCaption: Font { .system(size: 12.5, weight: .semibold) }
}

// MARK: - Tab Overview (Grid & List, Animated Close, Duplicate After Source, Drag Auto-Scroll!)

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
        guard !q.isEmpty else { return model.visibleTabs }
        return model.visibleTabs.filter {
            $0.title.lowercased().contains(q) || $0.url.lowercased().contains(q)
        }
    }

    var body: some View {
        NavigationStack {
            VStack(spacing: 0) {
                groupAndSearchHeader
                if useGridLayout {
                    gridTabBody
                } else {
                    listTabBody
                }
                bottomOverviewBar
            }
            .background(Color(red: 0.07, green: 0.07, blue: 0.08))
            .navigationTitle("탭 (\(model.visibleTabs.count))")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .topBarLeading) {
                    Button {
                        useGridLayout.toggle()
                    } label: {
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
                    Button("완료") { dismiss() }
                        .fontWeight(.semibold)
                }
            }
            .alert("새 탭 그룹", isPresented: $showingNewGroup) {
                TextField("그룹 이름", text: $newGroupName)
                Button("만들기") {
                    model.addGroup(newGroupName)
                    newGroupName = ""
                }
                Button("취소", role: .cancel) { newGroupName = "" }
            }
        }
        .preferredColorScheme(.dark)
    }

    private var groupAndSearchHeader: some View {
        VStack(spacing: 8) {
            ScrollView(.horizontal, showsIndicators: false) {
                HStack(spacing: 8) {
                    Menu {
                        ForEach(model.profiles) { profile in
                            Button {
                                model.selectProfile(profile.id)
                            } label: {
                                Label(profile.name, systemImage: profile.icon)
                            }
                        }
                    } label: {
                        HStack(spacing: 4) {
                            Image(systemName: model.activeProfile.icon)
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

                    Button {
                        showingNewGroup = true
                    } label: {
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
                TextField("탭 제목 또는 주소 검색", text: $searchQuery)
                    .font(.subheadline)
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
            withAnimation(.easeInOut(duration: 0.18)) {
                model.selectGroup(id)
            }
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

    // Grid View with Edge Auto-Scroll during drag & Animated Tab Close!
    private var gridTabBody: some View {
        GeometryReader { outerGeo in
            ScrollViewReader { proxy in
                ScrollView {
                    LazyVGrid(columns: [GridItem(.flexible(), spacing: 12), GridItem(.flexible(), spacing: 12)], spacing: 12) {
                        ForEach(filteredTabs) { tab in
                            tabGridCard(tab: tab, outerGeo: outerGeo, scrollProxy: proxy)
                                .id(tab.id)
                                .transition(.asymmetric(
                                    insertion: .scale(scale: 0.92).combined(with: .opacity),
                                    removal: .scale(scale: 0.78).combined(with: .opacity)
                                ))
                        }
                    }
                    .padding(12)
                    .animation(.spring(response: 0.28, dampingFraction: 0.82), value: model.tabs.map(\.id))
                }
            }
        }
    }

    private func tabGridCard(tab: BrowserTab, outerGeo: GeometryProxy, scrollProxy: ScrollViewProxy) -> some View {
        let isActive = tab.id == model.activeID
        return VStack(alignment: .leading, spacing: 0) {
            // Card Header
            HStack(spacing: 6) {
                Circle()
                    .fill(isActive ? Color.blue : Color.secondary.opacity(0.5))
                    .frame(width: 8, height: 8)
                Text(tab.title)
                    .font(.caption.weight(.semibold))
                    .lineLimit(1)
                Spacer(minLength: 2)
                Button {
                    withAnimation(.spring(response: 0.28, dampingFraction: 0.82)) {
                        model.close(tab.id)
                    }
                } label: {
                    Image(systemName: "xmark.circle.fill")
                        .font(.subheadline)
                        .foregroundStyle(.secondary)
                }
                .buttonStyle(.plain)
            }
            .padding(.horizontal, 10)
            .padding(.vertical, 8)
            .background(Color(red: 0.16, green: 0.16, blue: 0.19))

            // Thumbnail Area
            ZStack {
                Color(red: 0.12, green: 0.12, blue: 0.14)
                if let snap = model.tabSnapshots[tab.id] {
                    Image(uiImage: snap)
                        .resizable()
                        .scaledToFill()
                        .frame(height: 128)
                        .clipped()
                } else {
                    VStack(spacing: 6) {
                        Image(systemName: tab.url == "about:blank" ? "sparkles" : "globe")
                            .font(.title2)
                            .foregroundStyle(.secondary)
                        Text(URL(string: tab.url)?.host ?? tab.title)
                            .font(.caption2)
                            .foregroundStyle(.secondary)
                            .lineLimit(1)
                            .padding(.horizontal, 8)
                    }
                    .frame(height: 128)
                }
            }
        }
        .clipShape(RoundedRectangle(cornerRadius: 14, style: .continuous))
        .overlay(
            RoundedRectangle(cornerRadius: 14, style: .continuous)
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
            Button {
                UIPasteboard.general.string = tab.url
            } label: {
                Label("링크 복사", systemImage: "doc.on.doc")
            }
            Divider()
            Button(role: .destructive) {
                withAnimation(.spring(response: 0.28, dampingFraction: 0.82)) {
                    model.close(tab.id)
                }
            } label: {
                Label("이 탭 닫기", systemImage: "xmark")
            }
            Button(role: .destructive) {
                withAnimation(.spring(response: 0.28, dampingFraction: 0.82)) {
                    model.closeOtherTabs(keeping: tab.id)
                }
            } label: {
                Label("다른 탭 모두 닫기", systemImage: "xmark.circle")
            }
        }
        // Long-press drag reorder with top/bottom edge auto-scroll (Android v0.15.17 parity!)
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
                .onEnded { _ in
                    draggingTabID = nil
                }
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
                        withAnimation(.spring(response: 0.28, dampingFraction: 0.82)) {
                            model.duplicateTab(tab.id)
                        }
                    } label: {
                        Image(systemName: "plus.square.on.square")
                            .foregroundStyle(.secondary)
                    }
                    .buttonStyle(.plain)

                    Button {
                        withAnimation(.spring(response: 0.28, dampingFraction: 0.82)) {
                            model.close(tab.id)
                        }
                    } label: {
                        Image(systemName: "xmark.circle.fill")
                            .foregroundStyle(.secondary)
                    }
                    .buttonStyle(.plain)
                }
                .contentShape(Rectangle())
                .onTapGesture {
                    model.select(tab.id)
                    dismiss()
                }
            }
            .onMove(perform: model.reorder)
        }
        .listStyle(.plain)
        .environment(\.editMode, .constant(.active))
    }

    private var bottomOverviewBar: some View {
        HStack {
            Button {
                withAnimation(.spring(response: 0.28, dampingFraction: 0.82)) {
                    model.restoreClosedTab()
                }
            } label: {
                Label("닫은 탭 복원 (\(model.closedTabs.count))", systemImage: "arrow.uturn.backward")
                    .font(.caption.weight(.semibold))
            }
            .disabled(model.closedTabs.isEmpty)

            Spacer()

            Button(role: .destructive) {
                withAnimation(.spring(response: 0.28, dampingFraction: 0.82)) {
                    model.closeAllVisibleTabs()
                }
            } label: {
                Label("모두 닫기", systemImage: "trash")
                    .font(.caption.weight(.semibold))
            }
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 12)
        .background(Color(red: 0.11, green: 0.11, blue: 0.13))
    }
}

// MARK: - Bookmarks, History, Downloads, Page Tools, Profiles & Settings Sheets

struct BookmarksSheet: View {
    @EnvironmentObject private var model: BrowserModel
    @Environment(\.dismiss) private var dismiss
    @State private var search = ""
    @State private var showAdd = false
    @State private var newTitle = ""
    @State private var newURL = ""

    private var filtered: [BrowserBookmark] {
        let q = search.trimmingCharacters(in: .whitespaces).lowercased()
        guard !q.isEmpty else { return model.bookmarks }
        return model.bookmarks.filter { $0.title.lowercased().contains(q) || $0.url.lowercased().contains(q) }
    }

    var body: some View {
        NavigationStack {
            List {
                ForEach(filtered) { item in
                    Button {
                        model.open(item.url)
                        dismiss()
                    } label: {
                        VStack(alignment: .leading, spacing: 2) {
                            Text(item.title).font(.subheadline.weight(.semibold)).lineLimit(1)
                            Text(item.url).font(.caption2).foregroundStyle(.secondary).lineLimit(1)
                        }
                    }
                    .swipeActions {
                        Button("삭제", role: .destructive) {
                            model.bookmarks.removeAll { $0.id == item.id }
                            model.persist()
                        }
                    }
                }
            }
            .searchable(text: $search, prompt: "북마크 검색")
            .navigationTitle("북마크 (\(model.bookmarks.count))")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .topBarLeading) {
                    Button {
                        newTitle = model.activeTab?.title ?? ""
                        newURL = model.currentURL == "about:blank" ? "" : model.currentURL
                        showAdd = true
                    } label: {
                        Image(systemName: "plus")
                    }
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button("완료") { dismiss() }
                }
            }
            .alert("북마크 추가", isPresented: $showAdd) {
                TextField("제목", text: $newTitle)
                TextField("URL", text: $newURL)
                    .textInputAutocapitalization(.never)
                    .autocorrectionDisabled()
                Button("추가") {
                    guard !newURL.isEmpty else { return }
                    let u = newURL.contains("://") ? newURL : "https://" + newURL
                    model.bookmarks.append(BrowserBookmark(title: newTitle.isEmpty ? u : newTitle, url: u))
                    model.persist()
                }
                Button("취소", role: .cancel) {}
            }
        }
        .preferredColorScheme(.dark)
    }
}

struct HistorySheet: View {
    @EnvironmentObject private var model: BrowserModel
    @Environment(\.dismiss) private var dismiss
    @State private var search = ""

    private var filtered: [HistoryEntry] {
        let q = search.trimmingCharacters(in: .whitespaces).lowercased()
        guard !q.isEmpty else { return model.history }
        return model.history.filter { $0.title.lowercased().contains(q) || $0.url.lowercased().contains(q) }
    }

    var body: some View {
        NavigationStack {
            List {
                ForEach(filtered) { item in
                    Button {
                        model.open(item.url)
                        dismiss()
                    } label: {
                        VStack(alignment: .leading, spacing: 2) {
                            Text(item.title).font(.subheadline.weight(.semibold)).lineLimit(1)
                            Text(item.url).font(.caption2).foregroundStyle(.secondary).lineLimit(1)
                        }
                    }
                }
            }
            .searchable(text: $search, prompt: "방문 기록 검색")
            .navigationTitle("방문 기록")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .topBarLeading) {
                    Button("비우기", role: .destructive) {
                        model.history.removeAll()
                        model.persist()
                    }
                    .disabled(model.history.isEmpty)
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button("완료") { dismiss() }
                }
            }
        }
        .preferredColorScheme(.dark)
    }
}

struct DownloadsSheet: View {
    @EnvironmentObject private var model: BrowserModel
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        NavigationStack {
            List {
                if model.downloads.isEmpty {
                    Text("다운로드한 파일이 없습니다.")
                        .font(.subheadline)
                        .foregroundStyle(.secondary)
                } else {
                    ForEach(model.downloads, id: \.self) { file in
                        HStack {
                            Image(systemName: "doc.fill").foregroundStyle(.blue)
                            Text(file.lastPathComponent).lineLimit(1)
                            Spacer()
                            ShareLink(item: file) {
                                Image(systemName: "square.and.arrow.up")
                            }
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
                ToolbarItem(placement: .confirmationAction) {
                    Button("완료") { dismiss() }
                }
            }
        }
        .preferredColorScheme(.dark)
    }
}

struct PageToolsSheet: View {
    @Environment(\.dismiss) private var dismiss
    @ObservedObject var model: BrowserModel
    var onStartFind: () -> Void
    var onOpenCapture: () -> Void
    var onOpenWatermark: () -> Void
    var onOpenExtensions: () -> Void

    var body: some View {
        NavigationStack {
            List {
                Section("페이지 화면 배율 (\(Int((model.activeTab?.pageZoom ?? 1.0) * 100))%)") {
                    HStack {
                        Button("축소 (−)") {
                            model.setPageZoom((model.activeTab?.pageZoom ?? 1.0) - 0.1)
                        }
                        .buttonStyle(.bordered)
                        Spacer()
                        Button("100%") {
                            model.setPageZoom(1.0)
                        }
                        .buttonStyle(.bordered)
                        Spacer()
                        Button("확대 (＋)") {
                            model.setPageZoom((model.activeTab?.pageZoom ?? 1.0) + 0.1)
                        }
                        .buttonStyle(.bordered)
                    }
                }

                Section("페이지 도구") {
                    Button {
                        onStartFind()
                    } label: {
                        Label("페이지 내 찾기", systemImage: "magnifyingglass")
                    }
                    Button {
                        model.toggleDesktopMode()
                        dismiss()
                    } label: {
                        Label(
                            model.activeTab?.isDesktopMode == true ? "모바일 사이트로 전환" : "데스크톱 사이트 요청",
                            systemImage: "desktopcomputer"
                        )
                    }
                    Button {
                        onOpenCapture()
                    } label: {
                        Label("실시간 영역 캡처 (스크롤 스티칭)", systemImage: "viewfinder")
                    }
                    Button {
                        onOpenWatermark()
                    } label: {
                        Label("현재 화면 워터마크 제거", systemImage: "wand.and.stars")
                    }
                    Button {
                        onOpenExtensions()
                    } label: {
                        Label("확장 기능 · 유저스크립트 관리", systemImage: "puzzlepiece.extension")
                    }
                }
            }
            .navigationTitle("페이지 도구")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .confirmationAction) {
                    Button("닫기") { dismiss() }
                }
            }
        }
        .presentationDetents([.medium, .large])
        .preferredColorScheme(.dark)
    }
}

struct ProfileManagerSheet: View {
    @EnvironmentObject private var model: BrowserModel
    @Environment(\.dismiss) private var dismiss
    @State private var newName = ""
    @State private var showAdd = false

    var body: some View {
        NavigationStack {
            List {
                Section("프로필 선택") {
                    ForEach(model.profiles) { profile in
                        HStack {
                            Button {
                                model.selectProfile(profile.id)
                                dismiss()
                            } label: {
                                HStack {
                                    Image(systemName: profile.icon).foregroundStyle(.blue)
                                    Text(profile.name)
                                    Spacer()
                                    if profile.id == model.activeProfileID {
                                        Image(systemName: "checkmark").foregroundStyle(.blue)
                                    }
                                }
                            }
                            .buttonStyle(.plain)
                        }
                        .swipeActions {
                            if model.profiles.count > 1 {
                                Button("삭제", role: .destructive) {
                                    model.deleteProfile(profile.id)
                                }
                            }
                        }
                    }
                }
            }
            .navigationTitle("브라우징 프로필")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .topBarLeading) {
                    Button {
                        newName = ""
                        showAdd = true
                    } label: {
                        Image(systemName: "plus")
                    }
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button("완료") { dismiss() }
                }
            }
            .alert("새 프로필 만들기", isPresented: $showAdd) {
                TextField("프로필 이름 (예: 업무, 연구)", text: $newName)
                Button("만들기") {
                    model.addProfile(name: newName)
                }
                Button("취소", role: .cancel) {}
            }
        }
        .preferredColorScheme(.dark)
    }
}

struct SettingsSheet: View {
    @Environment(\.dismiss) private var dismiss
    @ObservedObject var model: BrowserModel
    var onOpenSync: () -> Void
    var onOpenExtensions: () -> Void
    var onOpenNetwork: () -> Void
    var onOpenAppLock: () -> Void

    var body: some View {
        NavigationStack {
            Form {
                Section("일반 · 검색") {
                    Picker("기본 검색 엔진", selection: $model.preferences.searchEngine) {
                        ForEach(SearchEngineChoice.allCases) { engine in
                            Text(engine.rawValue).tag(engine)
                        }
                    }
                    TextField("홈페이지 주소 (about:blank 또는 URL)", text: $model.preferences.homePage)
                        .textInputAutocapitalization(.never)
                        .autocorrectionDisabled()
                    Toggle("컴팩트 하단 툴바", isOn: $model.preferences.compactToolbar)
                }

                Section("탭 · 브라우징") {
                    Toggle("새 창 링크를 새 탭에서 열기", isOn: $model.preferences.openLinksInNewTab)
                    Toggle("탭 미리보기 썸네일 유지", isOn: $model.preferences.showTabThumbnails)
                    Toggle("자동 HTTPS 업그레이드", isOn: $model.preferences.automaticHTTPS)
                    Toggle("주소창에 전체 URL 표시", isOn: $model.preferences.showFullURL)
                }

                Section("하단 툴바 바로가기 (3개)") {
                    ForEach(0..<3, id: \.self) { idx in
                        if model.preferences.toolbarShortcuts.indices.contains(idx) {
                            Picker("바로가기 \(idx + 1)", selection: $model.preferences.toolbarShortcuts[idx]) {
                                ForEach(ToolbarShortcutAction.allCases) { act in
                                    Label(act.title, systemImage: act.systemImage).tag(act)
                                }
                            }
                        }
                    }
                }

                Section("핵심 기능 및 보안") {
                    Button(action: onOpenSync) {
                        Label("Google Drive 및 파일 암호화 동기화", systemImage: "arrow.triangle.2.circlepath.icloud")
                    }
                    Button(action: onOpenExtensions) {
                        Label("확장 기능 · 이미지 선로딩 · 유저스크립트", systemImage: "puzzlepiece.extension")
                    }
                    Button(action: onOpenNetwork) {
                        Label("DNS (1.1.1.1 / 8.8.8.8 DoH) · 프록시 설정", systemImage: "network.badge.shield.half.filled")
                    }
                    Button(action: onOpenAppLock) {
                        Label("앱 잠금 (Face ID · Touch ID · PIN)", systemImage: "lock.shield")
                    }
                }

                Section("개인정보 보호") {
                    Toggle("광고 및 서드파티 추적기 차단", isOn: $model.preferences.blockTrackers)
                    Toggle("자동 팝업 창 차단", isOn: $model.preferences.blockPopups)
                    Button("방문 기록 · 쿠키 · 웹 데이터 모두 지우기", role: .destructive) {
                        Task { await model.clearBrowsingData() }
                    }
                }

                Section("정보") {
                    LabeledContent("버전") {
                        Text("Omyaru iOS 0.2.0 · Android/PC 기능 패리티 빌드")
                            .foregroundStyle(.secondary)
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
                ToolbarItem(placement: .confirmationAction) {
                    Button("완료") { dismiss() }
                }
            }
        }
        .preferredColorScheme(.dark)
    }
}
