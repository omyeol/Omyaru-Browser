import SwiftUI
import WebKit
import UniformTypeIdentifiers

@main
struct OmyaruApp: App {
    @StateObject private var browser = BrowserModel()
    var body: some Scene {
        WindowGroup { BrowserScreen().environmentObject(browser) }
    }
}

struct BrowserTab: Codable, Identifiable, Hashable {
    var id: UUID = UUID()
    var title: String = "새 탭"
    var url: String = "about:blank"
    var groupID: UUID
    var isPrivate: Bool = false
}

struct BrowserGroup: Codable, Identifiable, Hashable {
    var id: UUID
    var name: String
}

struct BrowserBookmark: Codable, Identifiable, Hashable {
    var id: UUID = UUID()
    var title: String
    var url: String
}

@MainActor
final class BrowserModel: NSObject, ObservableObject, WKNavigationDelegate, WKUIDelegate, WKDownloadDelegate {
    static let privateGroup = UUID(uuidString: "00000000-0000-0000-0000-000000000001")!
    static let defaultGroup = UUID(uuidString: "00000000-0000-0000-0000-000000000002")!
    @Published var groups: [BrowserGroup]
    @Published var tabs: [BrowserTab]
    @Published var activeID: UUID
    @Published var activeGroupID: UUID
    @Published var bookmarks: [BrowserBookmark]
    @Published var history: [BrowserBookmark]
    @Published var downloads: [URL] = []
    @Published var status = ""
    private var webViews: [UUID: WKWebView] = [:]
    private static let stateKey = "omyaru.ios.browser.v1"

    private struct Saved: Codable {
        var groups: [BrowserGroup]
        var tabs: [BrowserTab]
        var activeID: UUID
        var activeGroupID: UUID
        var bookmarks: [BrowserBookmark]
        var history: [BrowserBookmark]
    }

    override init() {
        let saved = UserDefaults.standard.data(forKey: Self.stateKey).flatMap { try? JSONDecoder().decode(Saved.self, from: $0) }
        let defaultGroup = BrowserGroup(id: Self.defaultGroup, name: "개인")
        let initialTabs = saved?.tabs.filter { !$0.isPrivate } ?? []
        let initialTab = BrowserTab(groupID: Self.defaultGroup)
        groups = saved?.groups.isEmpty == false ? saved!.groups : [defaultGroup]
        tabs = initialTabs.isEmpty ? [initialTab] : initialTabs
        activeID = saved?.activeID ?? tabs[0].id
        activeGroupID = saved?.activeGroupID == Self.privateGroup ? Self.defaultGroup : (saved?.activeGroupID ?? Self.defaultGroup)
        bookmarks = saved?.bookmarks ?? []
        history = saved?.history ?? []
        super.init()
        if !tabs.contains(where: { $0.id == activeID }) { activeID = tabs[0].id }
    }

    var activeTab: BrowserTab? { tabs.first { $0.id == activeID } }
    var currentURL: String { activeTab?.url ?? "about:blank" }
    var visibleTabs: [BrowserTab] { tabs.filter { $0.groupID == activeGroupID } }
    var canGoBack: Bool { webViews[activeID]?.canGoBack ?? false }
    var canGoForward: Bool { webViews[activeID]?.canGoForward ?? false }

    func persist() {
        let regular = tabs.filter { !$0.isPrivate }
        let state = Saved(groups: groups, tabs: regular, activeID: activeID,
                          activeGroupID: activeGroupID, bookmarks: bookmarks, history: Array(history.prefix(500)))
        if let data = try? JSONEncoder().encode(state) { UserDefaults.standard.set(data, forKey: Self.stateKey) }
    }

    func webView(for id: UUID) -> WKWebView {
        if let existing = webViews[id] { return existing }
        let config = WKWebViewConfiguration()
        config.websiteDataStore = tabs.first(where: { $0.id == id })?.isPrivate == true ? .nonPersistent() : .default()
        config.allowsInlineMediaPlayback = true
        config.allowsPictureInPictureMediaPlayback = true
        let view = WKWebView(frame: .zero, configuration: config)
        view.allowsBackForwardNavigationGestures = true
        view.navigationDelegate = self
        view.uiDelegate = self
        webViews[id] = view
        if let tab = tabs.first(where: { $0.id == id }), tab.url != "about:blank", let url = URL(string: tab.url) {
            view.load(URLRequest(url: url))
        }
        return view
    }

    func open(_ entry: String) {
        let value = entry.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !value.isEmpty else { return }
        let candidate = value.contains("://") ? value : "https://" + value
        let resolved: URL?
        if value.contains(" ") || !value.contains(".") {
            let query = value.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed) ?? value
            resolved = URL(string: "https://duckduckgo.com/?q=\(query)")
        } else { resolved = URL(string: candidate) }
        guard let url = resolved else { return }
        webView(for: activeID).load(URLRequest(url: url))
        setTab(activeID) { $0.url = url.absoluteString }
    }

    func newTab(url: URL? = nil, background: Bool = false) {
        let privateTab = activeGroupID == Self.privateGroup
        let tab = BrowserTab(url: url?.absoluteString ?? "about:blank", groupID: activeGroupID, isPrivate: privateTab)
        tabs.append(tab)
        if !background { activeID = tab.id }
        persist()
    }

    func select(_ id: UUID) {
        guard let tab = tabs.first(where: { $0.id == id }) else { return }
        activeID = id
        activeGroupID = tab.groupID
        persist()
    }

    func close(_ id: UUID) {
        webViews.removeValue(forKey: id)?.stopLoading()
        tabs.removeAll { $0.id == id }
        if tabs.isEmpty { newTab() }
        if activeID == id { activeID = tabs.last!.id }
        persist()
    }

    func reorder(_ source: IndexSet, _ destination: Int) {
        let visible = visibleTabs
        var moved = visible
        moved.move(fromOffsets: source, toOffset: destination)
        var iterator = moved.makeIterator()
        tabs = tabs.map { $0.groupID == activeGroupID ? iterator.next()! : $0 }
        persist()
    }

    func selectGroup(_ id: UUID) {
        activeGroupID = id
        if let tab = tabs.first(where: { $0.groupID == id }) { activeID = tab.id }
        else { newTab() }
        persist()
    }

    func addGroup(_ name: String) {
        let trimmed = name.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return }
        let group = BrowserGroup(id: UUID(), name: trimmed)
        groups.append(group)
        selectGroup(group.id)
    }

    func back() { webViews[activeID]?.goBack() }
    func forward() { webViews[activeID]?.goForward() }
    func reload() { webViews[activeID]?.reload() }
    func home() { open("https://duckduckgo.com") }

    func addBookmark() {
        guard let tab = activeTab, tab.url != "about:blank" else { return }
        if !bookmarks.contains(where: { $0.url == tab.url }) {
            bookmarks.append(BrowserBookmark(title: tab.title, url: tab.url))
            persist()
        }
    }

    private func setTab(_ id: UUID, change: (inout BrowserTab) -> Void) {
        guard let index = tabs.firstIndex(where: { $0.id == id }) else { return }
        change(&tabs[index])
        persist()
    }

    private func id(for webView: WKWebView) -> UUID? { webViews.first { $0.value === webView }?.key }

    func webView(_ webView: WKWebView, didFinish navigation: WKNavigation!) {
        guard let id = id(for: webView) else { return }
        let url = webView.url?.absoluteString ?? "about:blank"
        setTab(id) { tab in tab.url = url; tab.title = webView.title ?? url }
        if tabs.first(where: { $0.id == id })?.isPrivate != true && url != "about:blank" {
            history.removeAll { $0.url == url }
            history.insert(BrowserBookmark(title: webView.title ?? url, url: url), at: 0)
            persist()
        }
        objectWillChange.send()
    }

    func webView(_ webView: WKWebView, didFailProvisionalNavigation navigation: WKNavigation!, withError error: Error) {
        status = error.localizedDescription
    }

    func webView(_ webView: WKWebView, createWebViewWith configuration: WKWebViewConfiguration,
                 for navigationAction: WKNavigationAction, windowFeatures: WKWindowFeatures) -> WKWebView? {
        if let url = navigationAction.request.url { newTab(url: url) }
        return nil
    }

    func webView(_ webView: WKWebView, decidePolicyFor navigationAction: WKNavigationAction,
                 decisionHandler: @escaping (WKNavigationActionPolicy) -> Void) {
        decisionHandler(navigationAction.shouldPerformDownload ? .download : .allow)
    }

    func webView(_ webView: WKWebView, navigationAction: WKNavigationAction, didBecome download: WKDownload) {
        download.delegate = self
    }

    func webView(_ webView: WKWebView, navigationResponse: WKNavigationResponse, didBecome download: WKDownload) {
        download.delegate = self
    }

    func download(_ download: WKDownload, decideDestinationUsing response: URLResponse,
                  suggestedFilename: String, completionHandler: @escaping (URL?) -> Void) {
        let directory = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask)[0].appendingPathComponent("Downloads")
        do {
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
            let name = URL(fileURLWithPath: suggestedFilename).lastPathComponent
            var destination = directory.appendingPathComponent(name)
            if FileManager.default.fileExists(atPath: destination.path) {
                destination = directory.appendingPathComponent(UUID().uuidString + "-" + name)
            }
            completionHandler(destination)
        } catch { completionHandler(nil); status = error.localizedDescription }
    }

    func downloadDidFinish(_ download: WKDownload) {
        let directory = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask)[0].appendingPathComponent("Downloads")
        downloads = (try? FileManager.default.contentsOfDirectory(at: directory, includingPropertiesForKeys: nil)) ?? []
        status = "다운로드 완료"
    }
}

struct BrowserWebView: UIViewRepresentable {
    let webView: WKWebView
    func makeUIView(context: Context) -> WKWebView { webView }
    func updateUIView(_ view: WKWebView, context: Context) {}
}

struct BrowserScreen: View {
    @EnvironmentObject private var model: BrowserModel
    @State private var address = ""
    @State private var showTabs = false
    @State private var showBookmarks = false
    @State private var showHistory = false
    @State private var showDownloads = false
    @State private var showBookshelf = false
    @State private var updateMessage: String?
    @FocusState private var addressFocused: Bool

    var body: some View {
        ZStack {
            Color(red: 0.08, green: 0.09, blue: 0.11).ignoresSafeArea()
            if let tab = model.activeTab { BrowserWebView(webView: model.webView(for: tab.id)).id(tab.id) }
        }
        .safeAreaInset(edge: .bottom) { toolbar }
        .sheet(isPresented: $showTabs) { TabOverview().environmentObject(model) }
        .sheet(isPresented: $showBookmarks) { BookmarkList(title: "북마크", items: model.bookmarks, open: model.open).environmentObject(model) }
        .sheet(isPresented: $showHistory) { BookmarkList(title: "방문 기록", items: model.history, open: model.open).environmentObject(model) }
        .sheet(isPresented: $showDownloads) { DownloadList().environmentObject(model) }
        .sheet(isPresented: $showBookshelf) { BookshelfView() }
        .alert("업데이트 확인", isPresented: Binding(get: { updateMessage != nil }, set: { if !$0 { updateMessage = nil } })) {
            Button("확인", role: .cancel) { updateMessage = nil }
        } message: { Text(updateMessage ?? "") }
        .onChange(of: model.activeID) { _ in address = model.currentURL == "about:blank" ? "" : model.currentURL }
        .onChange(of: model.currentURL) { value in if !addressFocused { address = value == "about:blank" ? "" : value } }
        .onAppear { address = model.currentURL == "about:blank" ? "" : model.currentURL }
        .preferredColorScheme(.dark)
    }

    private var toolbar: some View {
        VStack(spacing: 8) {
            HStack(spacing: 8) {
                Image(systemName: "lock.shield").foregroundStyle(.secondary)
                TextField("검색 또는 URL 입력", text: $address)
                    .textInputAutocapitalization(.never).autocorrectionDisabled()
                    .keyboardType(.URL).submitLabel(.go).focused($addressFocused)
                    .onSubmit { model.open(address); addressFocused = false }
                if !address.isEmpty { Button { address = "" } label: { Image(systemName: "xmark.circle.fill") } }
            }
            .padding(12).background(.regularMaterial, in: Capsule())
            HStack {
                Button(action: model.back) { Image(systemName: "chevron.left") }
                Spacer()
                Button(action: model.forward) { Image(systemName: "chevron.right") }
                Spacer()
                Button(action: model.home) { Image(systemName: "house") }
                Spacer()
                Button { showTabs = true } label: { Image(systemName: "square.on.square") }
                Spacer()
                Menu {
                    Button { model.newTab() } label: { Label("새 탭", systemImage: "plus") }
                    Button { model.addBookmark() } label: { Label("북마크에 추가", systemImage: "star") }
                    Button { showBookmarks = true } label: { Label("북마크", systemImage: "book") }
                    Button { showHistory = true } label: { Label("방문 기록", systemImage: "clock") }
                    Button { showDownloads = true } label: { Label("다운로드", systemImage: "arrow.down") }
                    Button { showBookshelf = true } label: { Label("책장", systemImage: "books.vertical") }
                    Button { model.reload() } label: { Label("새로고침", systemImage: "arrow.clockwise") }
                    Button { Task { await checkUpdates() } } label: { Label("업데이트 확인", systemImage: "arrow.triangle.2.circlepath") }
                } label: { Image(systemName: "ellipsis") }
            }.font(.system(size: 20)).padding(.horizontal, 12)
            if !model.status.isEmpty { Text(model.status).font(.caption).lineLimit(1).foregroundStyle(.secondary) }
        }
        .buttonStyle(.plain).padding(.horizontal, 12).padding(.top, 10).padding(.bottom, 4)
        .background(.ultraThinMaterial)
    }

    private func checkUpdates() async {
        guard let url = URL(string: "https://api.github.com/repos/omyeol/Omyaru-Browser/releases/tags/ios-latest") else { return }
        do {
            var request = URLRequest(url: url)
            request.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
            let (data, _) = try await URLSession.shared.data(for: request)
            let json = try JSONSerialization.jsonObject(with: data) as? [String: Any]
            let release = json?["name"] as? String ?? ""
            let installed = Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "0.1.0"
            updateMessage = release.contains(installed) ? "최신 버전(\(installed))입니다." : "새 버전: \(release). AltStore 소스에서 설치할 수 있습니다."
        } catch { updateMessage = "업데이트를 확인하지 못했습니다: \(error.localizedDescription)" }
    }
}

struct TabOverview: View {
    @EnvironmentObject private var model: BrowserModel
    @Environment(\.dismiss) private var dismiss
    @State private var newGroupName = ""
    @State private var showingNewGroup = false
    var body: some View {
        NavigationStack {
            List {
                Section {
                    ForEach(model.visibleTabs) { tab in
                        Button {
                            model.select(tab.id)
                            dismiss()
                        } label: {
                            VStack(alignment: .leading) {
                                Text(tab.title).lineLimit(1)
                                Text(tab.url).font(.caption).foregroundStyle(.secondary).lineLimit(1)
                            }
                        }
                        .swipeActions { Button("닫기", role: .destructive) { model.close(tab.id) } }
                    }.onMove(perform: model.reorder)
                } header: {
                    Menu {
                        Button("비공개") { model.selectGroup(BrowserModel.privateGroup) }
                        ForEach(model.groups) { group in Button(group.name) { model.selectGroup(group.id) } }
                        Button("새 그룹") { showingNewGroup = true }
                    } label: { Text(model.activeGroupID == BrowserModel.privateGroup ? "비공개" : model.groups.first(where: { $0.id == model.activeGroupID })?.name ?? "개인") }
                }
            }
            .navigationTitle("탭 목록")
            .toolbar {
                ToolbarItem(placement: .topBarLeading) { EditButton() }
                ToolbarItemGroup(placement: .topBarTrailing) {
                    Button { model.newTab() } label: { Image(systemName: "plus") }
                    Button("완료") { dismiss() }
                }
            }
            .alert("새 탭 그룹", isPresented: $showingNewGroup) {
                TextField("이름", text: $newGroupName)
                Button("만들기") { model.addGroup(newGroupName); newGroupName = "" }
                Button("취소", role: .cancel) { newGroupName = "" }
            }
        }
    }
}

struct BookmarkList: View {
    @Environment(\.dismiss) private var dismiss
    let title: String
    let items: [BrowserBookmark]
    let open: (String) -> Void
    var body: some View {
        NavigationStack {
            List(items) { item in
                Button {
                    open(item.url)
                    dismiss()
                } label: {
                    VStack(alignment: .leading) {
                        Text(item.title).lineLimit(1)
                        Text(item.url).font(.caption).foregroundStyle(.secondary).lineLimit(1)
                    }
                }
            }
            .navigationTitle(title)
            .toolbar { Button("완료") { dismiss() } }
        }
    }
}

struct DownloadList: View {
    @EnvironmentObject private var model: BrowserModel
    @Environment(\.dismiss) private var dismiss
    var body: some View {
        NavigationStack {
            List(model.downloads, id: \.self) { url in
                ShareLink(item: url) { Label(url.lastPathComponent, systemImage: "doc") }
            }
            .navigationTitle("다운로드")
            .toolbar { Button("완료") { dismiss() } }
        }
    }
}
