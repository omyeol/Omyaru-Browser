import SwiftUI
import UIKit

// MARK: - Fast Scroll Bar Overlay (Exact Android FastScrollBarView.kt Parity!)

struct FastScrollBarOverlay: View {
    @ObservedObject var model: BrowserModel
    @State private var isDragging = false
    @State private var dragStartFraction: Double = 0
    @State private var visible = false
    @State private var hideTask: Task<Void, Never>? = nil

    var body: some View {
        GeometryReader { geo in
            let height = max(1, geo.size.height)
            let thumbHeight = min(height * 0.7, max(72, height * CGFloat(model.scrollViewportRatio)))
            let maxY = max(1, height - thumbHeight)
            let thumbY = CGFloat(min(1, max(0, model.scrollFraction))) * maxY
            let thickness: CGFloat = isDragging ? 5.5 : 2.5

            if model.isScrollable {
                ZStack(alignment: .topTrailing) {
                    // NEVER cover the full right edge with hit-testing — doing so steals WKWebView's native right-to-left swipe-forward gesture!
                    Color.clear
                        .allowsHitTesting(false)

                    ZStack {
                        Capsule()
                            .fill(Color.black.opacity(0.22))
                            .frame(width: thickness + 1, height: thumbHeight + 1)
                            .offset(x: -0.5, y: 0.5)
                        Capsule()
                            .fill(Color(red: 0.36, green: 0.57, blue: 1.0, opacity: isDragging ? 0.92 : 0.72))
                            .frame(width: thickness, height: thumbHeight)
                            .overlay(
                                Capsule()
                                    .strokeBorder(Color.white.opacity(0.38), lineWidth: 0.6)
                            )
                    }
                    .frame(width: 26, height: thumbHeight, alignment: .trailing)
                    .padding(.trailing, 2)
                    .contentShape(Rectangle())
                    .offset(y: thumbY)
                    .opacity(visible || isDragging ? 1 : 0)
                    .allowsHitTesting(visible || isDragging)
                    .animation(.easeOut(duration: 0.18), value: isDragging)
                    .animation(.easeOut(duration: 0.28), value: visible)
                    .gesture(
                        DragGesture(minimumDistance: 4)
                            .onChanged { value in
                                // Let horizontal right-edge swipes pass through to WKWebView goForward!
                                if !isDragging && abs(value.translation.width) > abs(value.translation.height) {
                                    return
                                }
                                if !isDragging {
                                    isDragging = true
                                    visible = true
                                    hideTask?.cancel()
                                    dragStartFraction = model.scrollFraction
                                    UIImpactFeedbackGenerator(style: .light).impactOccurred()
                                }
                                let deltaFraction = Double(value.translation.height / maxY)
                                model.scrollToFraction(min(1, max(0, dragStartFraction + deltaFraction)))
                            }
                            .onEnded { _ in
                                if isDragging {
                                    isDragging = false
                                    scheduleFadeOut()
                                }
                            }
                    )
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topTrailing)
            }
        }
        .onChange(of: model.lastScrollActivity) { _ in
            guard model.isScrollable else {
                visible = false
                return
            }
            visible = true
            scheduleFadeOut()
        }
    }

    private func scheduleFadeOut() {
        hideTask?.cancel()
        hideTask = Task {
            try? await Task.sleep(nanoseconds: 1_500_000_000)
            guard !Task.isCancelled else { return }
            await MainActor.run {
                if !isDragging { visible = false }
            }
        }
    }
}

// MARK: - Native iOS Share Sheet (UIActivityViewController)

struct ActivityShareSheet: UIViewControllerRepresentable {
    let items: [Any]
    func makeUIViewController(context: Context) -> UIActivityViewController {
        UIActivityViewController(activityItems: items, applicationActivities: nil)
    }
    func updateUIViewController(_ uiViewController: UIActivityViewController, context: Context) {}
}

// MARK: - Android-Exact Browser Menu Sheet (Smart-Filtered: Hides Items Already Pinned on Toolbar!)

struct BrowserMenuSheet: View {
    @Environment(\.dismiss) private var dismiss
    @ObservedObject var model: BrowserModel
    var onAction: (ToolbarShortcutAction) -> Void
    var onOpenCaptureMenu: () -> Void
    var onOpenDriveSync: () -> Void
    var onOpenDownloads: () -> Void
    var onOpenToolbarEditor: () -> Void
    var onCheckUpdates: () -> Void

    private static let candidateOrder: [ToolbarShortcutAction] = [
        .bookmarks,
        .history,
        .capture,
        .watermark,
        .bookshelf,
        .vpn,
        .share,
        .copyAddress,
        .reopenClosedTab,
        .findInPage,
        .desktopSite,
        .contentBlocking,
        .extensions,
        .closeAllTabs,
        .settings
    ]

    private var visibleEntries: [ToolbarShortcutAction] {
        let pinned = Set(model.preferences.toolbarShortcuts.isEmpty
            ? ToolbarShortcutAction.defaultOrder
            : model.preferences.toolbarShortcuts)
        let filtered = Self.candidateOrder.filter { !pinned.contains($0) }
        return filtered.isEmpty ? [.settings] : filtered
    }

    var body: some View {
        NavigationStack {
            List {
                Section("브라우저 메뉴") {
                    ForEach(visibleEntries) { action in
                        Button {
                            dismiss()
                            DispatchQueue.main.asyncAfter(deadline: .now() + 0.16) {
                                if action == .capture {
                                    onOpenCaptureMenu()
                                } else {
                                    onAction(action)
                                }
                            }
                        } label: {
                            HStack(spacing: 12) {
                                Image(systemName: action.systemImage)
                                    .frame(width: 24)
                                    .foregroundStyle(action == .closeAllTabs ? .red : Color(red: 0.545, green: 0.671, blue: 1.0))
                                Text(menuTitle(for: action))
                                    .foregroundStyle(action == .closeAllTabs ? .red : .primary)
                                Spacer()
                                if action == .desktopSite && model.activeTab?.isDesktopMode == true {
                                    Text("켜짐").font(.caption).foregroundStyle(.blue)
                                } else if action == .contentBlocking {
                                    Text(model.isContentBlockingDisabledForCurrentHost ? "꺼짐" : "켜짐")
                                        .font(.caption)
                                        .foregroundStyle(model.isContentBlockingDisabledForCurrentHost ? .orange : .green)
                                }
                            }
                        }
                    }
                }

                Section("추가 도구") {
                    Button {
                        dismiss()
                        DispatchQueue.main.asyncAfter(deadline: .now() + 0.16) {
                            if let active = model.activeTab { model.duplicateTab(active.id) }
                        }
                    } label: {
                        Label("현재 탭 복제 (바로 뒤에 생성)", systemImage: "plus.square.on.square")
                    }
                    Button {
                        dismiss()
                        DispatchQueue.main.asyncAfter(deadline: .now() + 0.16) { onOpenDownloads() }
                    } label: {
                        Label("다운로드 (\(model.downloads.count))", systemImage: "arrow.down.circle")
                    }
                    Button {
                        dismiss()
                        DispatchQueue.main.asyncAfter(deadline: .now() + 0.16) { onOpenDriveSync() }
                    } label: {
                        Label("Google Drive 동기화", systemImage: "arrow.triangle.2.circlepath.icloud")
                    }
                    Button {
                        dismiss()
                        DispatchQueue.main.asyncAfter(deadline: .now() + 0.16) { onOpenToolbarEditor() }
                    } label: {
                        Label("하단 툴바 버튼 편집", systemImage: "slider.horizontal.below.rectangle")
                    }
                    Button {
                        dismiss()
                        DispatchQueue.main.asyncAfter(deadline: .now() + 0.16) { onCheckUpdates() }
                    } label: {
                        Label("업데이트 확인 (v\(BrowserModel.appVersionName))", systemImage: "arrow.triangle.2.circlepath")
                    }
                }
            }
            .navigationTitle("메뉴")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .confirmationAction) {
                    Button("닫기") { dismiss() }
                }
            }
        }
        .presentationDetents([.medium, .large])
    }

    private func menuTitle(for action: ToolbarShortcutAction) -> String {
        switch action {
        case .contentBlocking:
            return model.isContentBlockingDisabledForCurrentHost ? "이 사이트 콘텐츠 차단 켜기" : "이 사이트 콘텐츠 차단 끄기"
        case .desktopSite:
            return model.activeTab?.isDesktopMode == true ? "모바일 사이트 요청" : "데스크톱 사이트 요청"
        default:
            return action.title
        }
    }
}

// MARK: - Android-Exact Capture Action Menu Sheet (showCaptureMenu() Parity!)

struct CaptureActionMenuSheet: View {
    @Environment(\.dismiss) private var dismiss
    var onOpenLiveCapture: () -> Void
    var onOpenWatermark: () -> Void
    var onOpenBookshelf: () -> Void

    var body: some View {
        NavigationStack {
            List {
                Button {
                    dismiss()
                    DispatchQueue.main.asyncAfter(deadline: .now() + 0.16) { onOpenLiveCapture() }
                } label: {
                    Label("영역 캡처 (책장 추가 · PNG 저장 · 복사 · 아래로 확장)", systemImage: "viewfinder")
                }
                Button {
                    dismiss()
                    DispatchQueue.main.asyncAfter(deadline: .now() + 0.16) { onOpenWatermark() }
                } label: {
                    Label("워터마크 제거 (내장 복원 · 웹 도구 ⚡ 사진 주입)", systemImage: "wand.and.stars")
                }
                Button {
                    dismiss()
                    DispatchQueue.main.asyncAfter(deadline: .now() + 0.16) { onOpenBookshelf() }
                } label: {
                    Label("책장 열기", systemImage: "books.vertical")
                }
            }
            .navigationTitle("화면 캡처")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .confirmationAction) {
                    Button("닫기") { dismiss() }
                }
            }
        }
        .presentationDetents([.height(260)])
    }
}

// MARK: - Android-Exact Bookmark Edit Sheet (BookmarkEditDialog.kt Parity!)

struct BookmarkEditSheet: View {
    @Environment(\.dismiss) private var dismiss
    @ObservedObject var model: BrowserModel
    let existing: BrowserBookmark?
    let defaultTitle: String
    let defaultURL: String

    @State private var titleText: String = ""
    @State private var urlText: String = ""
    @State private var selectedFolderID: String? = nil
    @State private var showNewFolderAlert = false
    @State private var newFolderName = ""

    var body: some View {
        NavigationStack {
            Form {
                Section("북마크 정보") {
                    TextField("이름", text: $titleText)
                    TextField("주소 (URL)", text: $urlText)
                        .textInputAutocapitalization(.never)
                        .autocorrectionDisabled()
                        .keyboardType(.URL)
                }

                Section("저장할 폴더") {
                    Picker("폴더", selection: $selectedFolderID) {
                        Text("최상위 북마크").tag(Optional<String>.none)
                        ForEach(model.bookmarkFolders) { folder in
                            Text(folder.name).tag(Optional(folder.id))
                        }
                    }
                    Button {
                        newFolderName = ""
                        showNewFolderAlert = true
                    } label: {
                        Label("새 북마크 폴더 만들기", systemImage: "folder.badge.plus")
                    }
                }

                if let existing {
                    Section {
                        Button(role: .destructive) {
                            model.deleteBookmark(existing.id)
                            dismiss()
                        } label: {
                            Label("북마크 삭제", systemImage: "trash")
                        }
                    }
                }
            }
            .navigationTitle(existing == nil ? "북마크 추가" : "북마크 편집")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("취소") { dismiss() }
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button("저장") {
                        model.saveBookmark(editingID: existing?.id, title: titleText, url: urlText, folderID: selectedFolderID)
                        dismiss()
                    }
                    .fontWeight(.semibold)
                    .disabled(urlText.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                }
            }
            .alert("새 북마크 폴더", isPresented: $showNewFolderAlert) {
                TextField("폴더 이름", text: $newFolderName)
                Button("만들기") {
                    let trimmed = newFolderName.trimmingCharacters(in: .whitespacesAndNewlines)
                    guard !trimmed.isEmpty else { return }
                    let created = BookmarkFolder(name: trimmed, parentID: nil)
                    model.bookmarkFolders.append(created)
                    model.persist()
                    selectedFolderID = created.id
                }
                Button("취소", role: .cancel) {}
            }
            .onAppear {
                titleText = existing?.title ?? defaultTitle
                urlText = existing?.url ?? defaultURL
                selectedFolderID = existing?.folderID
            }
        }
        .presentationDetents([.medium, .large])
    }
}

// MARK: - Android-Exact Web Element Long-Press Context Menu Sheet (showContextElementMenu Parity!)

struct WebContextElementSheet: View {
    @Environment(\.dismiss) private var dismiss
    @ObservedObject var model: BrowserModel
    let element: WebContextElement
    var onShareURL: (String) -> Void

    @State private var pickingShelfFolder = false
    @ObservedObject private var shelf = ShelfModel.shared
    @State private var selectedShelfFolder: String = ShelfModel.shared.activeFolder

    var body: some View {
        NavigationStack {
            List {
                if element.kind == .image, let mediaURL = element.mediaURL {
                    Section("이미지 작업") {
                        Button {
                            if let url = URL(string: mediaURL) { _ = model.newTab(url: url, afterTabID: model.activeID) }
                            dismiss()
                        } label: {
                            Label("새 탭에서 이미지 열기", systemImage: "plus.rectangle.on.rectangle")
                        }
                        Button {
                            model.openInBackgroundTab(mediaURL)
                            dismiss()
                        } label: {
                            Label("백그라운드 탭에서 이미지 열기", systemImage: "rectangle.stack.badge.plus")
                        }
                        Button {
                            UIPasteboard.general.string = mediaURL
                            model.status = "이미지 주소를 복사했습니다."
                            dismiss()
                        } label: {
                            Label("이미지 주소 복사", systemImage: "doc.on.doc")
                        }
                        Button {
                            dismiss()
                            Task { await model.downloadRemoteURL(mediaURL, saveToBookshelfFolder: nil) }
                        } label: {
                            Label("이미지 저장", systemImage: "arrow.down.circle")
                        }
                        Button {
                            dismiss()
                            DispatchQueue.main.asyncAfter(deadline: .now() + 0.16) { onShareURL(mediaURL) }
                        } label: {
                            Label("이미지 공유", systemImage: "square.and.arrow.up")
                        }
                        Button {
                            selectedShelfFolder = shelf.activeFolder
                            pickingShelfFolder = true
                        } label: {
                            Label("책장에 저장", systemImage: "books.vertical.fill")
                                .foregroundStyle(.blue)
                                .fontWeight(.semibold)
                        }
                    }
                } else if element.kind == .video, let vURL = element.mediaURL ?? element.linkURL {
                    Section("동영상 작업") {
                        Button {
                            if let url = URL(string: vURL) { _ = model.newTab(url: url, afterTabID: model.activeID) }
                            dismiss()
                        } label: {
                            Label("새 탭에서 동영상 열기", systemImage: "play.rectangle")
                        }
                        Button {
                            model.openInBackgroundTab(vURL)
                            dismiss()
                        } label: {
                            Label("백그라운드 탭에서 동영상 열기", systemImage: "rectangle.stack.badge.plus")
                        }
                        Button {
                            UIPasteboard.general.string = vURL
                            model.status = "동영상 주소를 복사했습니다."
                            dismiss()
                        } label: {
                            Label("동영상 주소 복사", systemImage: "doc.on.doc")
                        }
                        Button {
                            dismiss()
                            Task { await model.downloadRemoteURL(vURL, saveToBookshelfFolder: nil) }
                        } label: {
                            Label("동영상 저장", systemImage: "arrow.down.circle")
                        }
                        Button {
                            dismiss()
                            DispatchQueue.main.asyncAfter(deadline: .now() + 0.16) { onShareURL(vURL) }
                        } label: {
                            Label("동영상 공유", systemImage: "square.and.arrow.up")
                        }
                    }
                }

                if let linkURL = element.linkURL, linkURL != element.mediaURL {
                    Section("링크 작업") {
                        Button {
                            if let url = URL(string: linkURL) { _ = model.newTab(url: url, afterTabID: model.activeID) }
                            dismiss()
                        } label: {
                            Label("새 탭에서 링크 열기", systemImage: "plus.rectangle.on.rectangle")
                        }
                        Button {
                            model.openInBackgroundTab(linkURL)
                            dismiss()
                        } label: {
                            Label("백그라운드 탭에서 링크 열기", systemImage: "rectangle.stack.badge.plus")
                        }
                        Button {
                            UIPasteboard.general.string = linkURL
                            model.status = "링크 주소를 복사했습니다."
                            dismiss()
                        } label: {
                            Label("링크 주소 복사", systemImage: "doc.on.doc")
                        }
                        Button {
                            dismiss()
                            DispatchQueue.main.asyncAfter(deadline: .now() + 0.16) { onShareURL(linkURL) }
                        } label: {
                            Label("링크 공유", systemImage: "square.and.arrow.up")
                        }
                    }
                }
            }
            .navigationTitle(element.title)
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .confirmationAction) {
                    Button("닫기") { dismiss() }
                }
            }
            .sheet(isPresented: $pickingShelfFolder) {
                CaptureFolderPickerSheet(
                    shelf: shelf,
                    selectedFolder: $selectedShelfFolder,
                    onConfirm: { folder in
                        pickingShelfFolder = false
                        let imgURL = element.mediaURL ?? ""
                        dismiss()
                        Task { await model.downloadRemoteURL(imgURL, saveToBookshelfFolder: folder) }
                    }
                )
            }
        }
        .presentationDetents([.medium, .large])
    }
}

// MARK: - 5-Second Safety Countdown Sheet for Close All Tabs (Android confirmCloseAllTabs() Parity!)

struct CloseAllTabsCountdownSheet: View {
    @Environment(\.dismiss) private var dismiss
    let tabCount: Int
    var onConfirmCloseAll: () -> Void
    @State private var remainingSeconds = 5

    var body: some View {
        NavigationStack {
            VStack(spacing: 18) {
                Image(systemName: "exclamationmark.triangle.fill")
                    .font(.system(size: 40))
                    .foregroundStyle(.red)
                Text("현재 그룹의 모든 탭(\(tabCount)개)을 닫으시겠습니까?")
                    .font(.headline)
                    .multilineTextAlignment(.center)
                Text("실수로 탭을 한꺼번에 닫는 것을 방지하기 위해 5초 후 확인 버튼이 활성화됩니다.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .multilineTextAlignment(.center)
                HStack(spacing: 12) {
                    Button("취소") { dismiss() }
                        .buttonStyle(.bordered)
                        .frame(maxWidth: .infinity)
                    Button(role: .destructive) {
                        onConfirmCloseAll()
                        dismiss()
                    } label: {
                        Text(remainingSeconds > 0 ? "확인 (\(remainingSeconds)초)" : "모두 닫기")
                            .frame(maxWidth: .infinity)
                    }
                    .buttonStyle(.borderedProminent)
                    .tint(.red)
                    .disabled(remainingSeconds > 0)
                }
                .padding(.top, 4)
            }
            .padding(24)
            .navigationTitle("모든 탭 닫기")
            .navigationBarTitleDisplayMode(.inline)
        }
        .presentationDetents([.height(270)])
        .task {
            while remainingSeconds > 0 {
                try? await Task.sleep(nanoseconds: 1_000_000_000)
                if Task.isCancelled { return }
                remainingSeconds = max(0, remainingSeconds - 1)
            }
        }
    }
}

// MARK: - Tab Group Manager Sheet (Android showGroupMenu() Parity!)

struct TabGroupManagerSheet: View {
    @Environment(\.dismiss) private var dismiss
    @ObservedObject var model: BrowserModel
    @State private var newGroupName = ""
    @State private var showCreateAlert = false
    @State private var renamingGroup: BrowserGroup? = nil
    @State private var renameText = ""

    var body: some View {
        NavigationStack {
            List {
                Section("워크스페이스 · 탭 그룹 전환") {
                    Button {
                        model.selectGroup(BrowserModel.privateGroupID)
                        dismiss()
                    } label: {
                        HStack {
                            Label("비공개 브라우징", systemImage: "hand.raised.fill")
                            Spacer()
                            Text("\(model.tabs.filter { $0.isPrivate }.count)개").font(.caption).foregroundStyle(.secondary)
                            if model.activeGroupID == BrowserModel.privateGroupID {
                                Image(systemName: "checkmark").foregroundStyle(.blue)
                            }
                        }
                    }

                    ForEach(model.profileGroups) { group in
                        let count = model.tabs.filter { !$0.isPrivate && $0.profileID == model.activeProfileID && $0.groupID == group.id }.count
                        HStack {
                            Button {
                                model.selectGroup(group.id)
                                dismiss()
                            } label: {
                                HStack {
                                    Label(group.name, systemImage: "folder")
                                    Spacer()
                                    Text("\(count)개").font(.caption).foregroundStyle(.secondary)
                                    if model.activeGroupID == group.id {
                                        Image(systemName: "checkmark").foregroundStyle(.blue)
                                    }
                                }
                            }
                            .buttonStyle(.plain)

                            Menu {
                                Button {
                                    renamingGroup = group
                                    renameText = group.name
                                } label: {
                                    Label("그룹 이름 변경", systemImage: "pencil")
                                }
                                if model.profiles.count > 1 {
                                    Menu("다른 프로필로 이동") {
                                        ForEach(model.profiles.filter { $0.id != model.activeProfileID }) { target in
                                            Button {
                                                model.moveGroup(group.id, toProfile: target.id)
                                                dismiss()
                                            } label: {
                                                Label(target.name, systemImage: target.icon)
                                            }
                                        }
                                    }
                                }
                                if model.profileGroups.count > 1 {
                                    Button(role: .destructive) {
                                        model.deleteGroup(group.id)
                                    } label: {
                                        Label("그룹 삭제", systemImage: "trash")
                                    }
                                }
                            } label: {
                                Image(systemName: "ellipsis.circle")
                                    .foregroundStyle(.secondary)
                                    .padding(.leading, 6)
                            }
                        }
                    }
                }

                Section {
                    Button {
                        newGroupName = ""
                        showCreateAlert = true
                    } label: {
                        Label("새 탭 그룹 만들기", systemImage: "folder.badge.plus")
                    }
                }
            }
            .navigationTitle("탭 그룹 (\(model.activeProfile.name))")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .confirmationAction) {
                    Button("닫기") { dismiss() }
                }
            }
            .alert("새 탭 그룹", isPresented: $showCreateAlert) {
                TextField("그룹 이름", text: $newGroupName)
                Button("만들기") {
                    model.addGroup(newGroupName)
                    dismiss()
                }
                Button("취소", role: .cancel) {}
            }
            .alert("그룹 이름 변경", isPresented: Binding(get: { renamingGroup != nil }, set: { if !$0 { renamingGroup = nil } })) {
                TextField("새 그룹 이름", text: $renameText)
                Button("변경") {
                    if let g = renamingGroup { model.renameGroup(g.id, to: renameText) }
                    renamingGroup = nil
                }
                Button("취소", role: .cancel) { renamingGroup = nil }
            }
        }
        .presentationDetents([.medium, .large])
    }
}

// MARK: - Batch Tab Move Sheet (Move 1 or Multiple Selected Tabs to Any Profile / Group)

struct TabBatchMoveSheet: View {
    @Environment(\.dismiss) private var dismiss
    @ObservedObject var model: BrowserModel
    let tabIDs: Set<UUID>
    var onMoved: () -> Void

    var body: some View {
        NavigationStack {
            List {
                Section("비공개") {
                    Button {
                        model.moveTabs(tabIDs, toProfile: model.activeProfileID, groupID: BrowserModel.privateGroupID)
                        onMoved()
                        dismiss()
                    } label: {
                        Label("비공개 브라우징으로 이동", systemImage: "hand.raised.fill")
                    }
                }

                ForEach(model.profiles) { profile in
                    Section(profile.name) {
                        let pGroups = model.groups.filter { $0.profileID == profile.id }
                        ForEach(pGroups) { group in
                            Button {
                                model.moveTabs(tabIDs, toProfile: profile.id, groupID: group.id)
                                onMoved()
                                dismiss()
                            } label: {
                                Label("\(profile.name) · \(group.name)", systemImage: profile.icon)
                            }
                        }
                    }
                }
            }
            .navigationTitle("탭 이동 (\(tabIDs.count)개)")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("취소") { dismiss() }
                }
            }
        }
        .presentationDetents([.medium, .large])
    }
}
