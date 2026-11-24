import SwiftUI
import UIKit

// MARK: - iOS 26 Safari Liquid Glass Design Primitives (Solid Flat by Default, Liquid Glass Only When Enabled)

private struct LiquidGlassEnabledKey: EnvironmentKey {
    static let defaultValue: Bool = false
}

extension EnvironmentValues {
    var isLiquidGlassEnabled: Bool {
        get { self[LiquidGlassEnabledKey.self] }
        set { self[LiquidGlassEnabledKey.self] = newValue }
    }
}

struct LiquidGlassShapeBackground<S: InsettableShape>: View {
    @Environment(\.isLiquidGlassEnabled) private var isLiquidGlassEnabled
    let shape: S
    var tintOpacity: Double = 0.46
    var rimOpacity: Double = 0.34
    var specularOpacity: Double = 0.22
    var shadowRadius: CGFloat = 14

    var body: some View {
        if isLiquidGlassEnabled {
            ZStack {
                shape.fill(.ultraThinMaterial)
                shape.fill(Color(red: 0.08, green: 0.09, blue: 0.11, opacity: tintOpacity))
                shape.fill(
                    LinearGradient(
                        colors: [
                            Color.white.opacity(specularOpacity),
                            Color.white.opacity(specularOpacity * 0.22),
                            Color.clear
                        ],
                        startPoint: .topLeading,
                        endPoint: .bottomTrailing
                    )
                )
            }
            .overlay(
                shape.strokeBorder(
                    LinearGradient(
                        colors: [
                            Color.white.opacity(rimOpacity),
                            Color.white.opacity(rimOpacity * 0.32),
                            Color.white.opacity(rimOpacity * 0.65)
                        ],
                        startPoint: .topLeading,
                        endPoint: .bottomTrailing
                    ),
                    lineWidth: 1.0
                )
            )
            .shadow(color: Color.black.opacity(0.36), radius: shadowRadius, x: 0, y: 6)
        } else {
            shape
                .fill(Color(red: 0.175, green: 0.180, blue: 0.198))
                .overlay(
                    shape.strokeBorder(Color.white.opacity(0.09), lineWidth: 0.8)
                )
                .shadow(color: Color.black.opacity(0.22), radius: min(8, shadowRadius), x: 0, y: 3)
        }
    }
}

struct LiquidGlassPressStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .scaleEffect(configuration.isPressed ? 0.92 : 1.0)
            .brightness(configuration.isPressed ? 0.08 : 0.0)
            .animation(.spring(response: 0.22, dampingFraction: 0.72), value: configuration.isPressed)
    }
}

struct LiquidGlassCircleButton: View {
    @Environment(\.isLiquidGlassEnabled) private var isLiquidGlassEnabled
    let systemImage: String
    var size: CGFloat = 44
    var iconSize: CGFloat = 17
    var tint: Color? = nil
    var action: () -> Void

    var body: some View {
        Button(action: action) {
            ZStack {
                if let tint {
                    if isLiquidGlassEnabled {
                        Circle()
                            .fill(
                                LinearGradient(
                                    colors: [tint.opacity(0.95), tint],
                                    startPoint: .topLeading,
                                    endPoint: .bottomTrailing
                                )
                            )
                            .overlay(
                                Circle()
                                    .strokeBorder(
                                        LinearGradient(
                                            colors: [Color.white.opacity(0.55), Color.white.opacity(0.12)],
                                            startPoint: .topLeading,
                                            endPoint: .bottomTrailing
                                        ),
                                        lineWidth: 1.0
                                    )
                            )
                            .shadow(color: tint.opacity(0.45), radius: 10, y: 4)
                    } else {
                        Circle()
                            .fill(tint)
                            .shadow(color: Color.black.opacity(0.24), radius: 6, y: 2)
                    }
                } else {
                    LiquidGlassShapeBackground(shape: Circle(), tintOpacity: 0.36, rimOpacity: 0.36, specularOpacity: 0.24, shadowRadius: 10)
                }

                Image(systemName: systemImage)
                    .font(.system(size: iconSize, weight: .semibold))
                    .foregroundStyle(.white)
            }
            .frame(width: size, height: size)
            .contentShape(Circle())
        }
        .buttonStyle(LiquidGlassPressStyle())
    }
}

// MARK: - Morphing Liquid Glass Popover Container (Exact Video 00:06->00:08 & 00:22->00:25 Bloom!)

struct LiquidGlassMorphContainer<Content: View>: View {
    let isPresented: Bool
    let alignment: Alignment
    let anchor: UnitPoint
    let edgeInsets: EdgeInsets
    var animationResponse: Double = 0.30
    var animationDamping: Double = 0.84
    var initialScale: CGFloat = 0.42
    var initialBlur: CGFloat = 10
    let onDismiss: () -> Void
    @ViewBuilder let content: () -> Content

    var body: some View {
        ZStack(alignment: alignment) {
            if isPresented {
                Color.black.opacity(0.18)
                    .ignoresSafeArea()
                    .contentShape(Rectangle())
                    .onTapGesture {
                        withAnimation(.spring(response: animationResponse, dampingFraction: animationDamping)) {
                            onDismiss()
                        }
                    }
                    .transition(.opacity)

                content()
                    .padding(edgeInsets)
                    .transition(
                        .asymmetric(
                            insertion: .modifier(
                                active: LiquidGlassBloomModifier(scale: initialScale, blur: initialBlur, opacity: 0, anchor: anchor),
                                identity: LiquidGlassBloomModifier(scale: 1.0, blur: 0, opacity: 1, anchor: anchor)
                            ),
                            removal: .modifier(
                                active: LiquidGlassBloomModifier(scale: min(0.92, initialScale + 0.04), blur: max(2, initialBlur - 1), opacity: 0, anchor: anchor),
                                identity: LiquidGlassBloomModifier(scale: 1.0, blur: 0, opacity: 1, anchor: anchor)
                            )
                        )
                    )
            }
        }
        .animation(.spring(response: animationResponse, dampingFraction: animationDamping), value: isPresented)
    }
}

private struct LiquidGlassBloomModifier: ViewModifier {
    let scale: CGFloat
    let blur: CGFloat
    let opacity: Double
    let anchor: UnitPoint

    func body(content: Content) -> some View {
        content
            .blur(radius: blur)
            .scaleEffect(scale, anchor: anchor)
            .opacity(opacity)
    }
}

private struct GlassMenuRow: View {
    let title: String
    var subtitle: String? = nil
    let systemImage: String
    var iconColor: Color = .white
    var textColor: Color = .white
    var trailingText: String? = nil
    var trailingChevron: Bool = false
    var action: () -> Void

    var body: some View {
        Button(action: action) {
            HStack(spacing: 12) {
                Image(systemName: systemImage)
                    .font(.system(size: 16, weight: .medium))
                    .foregroundStyle(iconColor)
                    .frame(width: 24, alignment: .center)

                VStack(alignment: .leading, spacing: 1) {
                    Text(title)
                        .font(.system(size: 15, weight: .medium))
                        .foregroundStyle(textColor)
                        .lineLimit(1)
                    if let subtitle, !subtitle.isEmpty {
                        Text(subtitle)
                            .font(.system(size: 11.5))
                            .foregroundStyle(.white.opacity(0.62))
                            .lineLimit(1)
                    }
                }

                Spacer(minLength: 4)

                if let trailingText {
                    Text(trailingText)
                        .font(.caption.weight(.semibold))
                        .foregroundStyle(.white.opacity(0.65))
                }
                if trailingChevron {
                    Image(systemName: "chevron.right")
                        .font(.system(size: 12, weight: .semibold))
                        .foregroundStyle(.white.opacity(0.55))
                }
            }
            .padding(.horizontal, 16)
            .padding(.vertical, 11)
            .contentShape(Rectangle())
        }
        .buttonStyle(LiquidGlassPressStyle())
    }
}

// MARK: - 1. Page Tools Morphing Popover (Exact Frames 00:06 -> 00:07 -> 00:08, User-Customizable!)

struct LiquidGlassPageToolsPopover: View {
    @ObservedObject var model: BrowserModel
    var onDismiss: () -> Void
    var onBookmarkEdit: () -> Void
    var onFindInPage: () -> Void
    var onOpenCapture: () -> Void
    var onOpenWatermark: () -> Void
    var onOpenExtensions: () -> Void
    var onToolbarAction: (ToolbarShortcutAction) -> Void
    var onOpenDownloads: () -> Void
    var onOpenDriveSync: () -> Void

    private var zoomPercent: Int {
        Int(((model.activeTab?.pageZoom ?? model.preferences.fontScale) * 100).rounded())
    }

    private var activeItems: [PageToolsMenuItem] {
        let configured = model.preferences.pageToolsItems
        return configured.isEmpty ? PageToolsMenuItem.defaultOrder : configured
    }

    private var pinnedTopZoom: Bool {
        activeItems.first == .zoomControls
    }

    private var scrollableItems: [PageToolsMenuItem] {
        pinnedTopZoom ? Array(activeItems.dropFirst()) : activeItems
    }

    private var zoomControlsBar: some View {
        HStack(spacing: 8) {
            Button {
                model.setPageZoom((model.activeTab?.pageZoom ?? model.preferences.fontScale) - 0.05)
            } label: {
                Image(systemName: "minus")
                    .font(.system(size: 14, weight: .bold))
                    .foregroundStyle(.white)
                    .frame(width: 34, height: 32)
                    .background(Color.white.opacity(0.12), in: Capsule())
            }
            .buttonStyle(LiquidGlassPressStyle())

            Button {
                model.setPageZoom(1.0)
            } label: {
                Text("배율 \(zoomPercent)%")
                    .font(.system(size: 13.5, weight: .semibold).monospacedDigit())
                    .foregroundStyle(.white)
                    .frame(maxWidth: .infinity)
                    .frame(height: 32)
                    .background(Color.white.opacity(0.08), in: Capsule())
            }
            .buttonStyle(LiquidGlassPressStyle())

            Button {
                model.setPageZoom((model.activeTab?.pageZoom ?? model.preferences.fontScale) + 0.05)
            } label: {
                Image(systemName: "plus")
                    .font(.system(size: 14, weight: .bold))
                    .foregroundStyle(.white)
                    .frame(width: 34, height: 32)
                    .background(Color.white.opacity(0.12), in: Capsule())
            }
            .buttonStyle(LiquidGlassPressStyle())
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 10)
    }

    @ViewBuilder
    private func rowView(for item: PageToolsMenuItem) -> some View {
        switch item {
        case .zoomControls:
            zoomControlsBar

        case .bookmarkEdit:
            GlassMenuRow(
                title: model.isCurrentBookmarked ? "북마크 편집…" : "북마크에 추가",
                systemImage: model.isCurrentBookmarked ? "bookmark.fill" : "bookmark",
                iconColor: model.isCurrentBookmarked ? Color(red: 0.45, green: 0.68, blue: 1.0) : .white
            ) {
                onDismiss()
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.14) { onBookmarkEdit() }
            }

        case .findInPage:
            GlassMenuRow(title: "페이지에서 찾기", systemImage: "doc.text.magnifyingglass") {
                onDismiss()
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.14) { onFindInPage() }
            }

        case .contentBlocking:
            GlassMenuRow(
                title: model.isContentBlockingDisabledForCurrentHost ? "콘텐츠 차단기 켜기" : "콘텐츠 차단기 끄기",
                systemImage: "shield.lefthalf.filled",
                iconColor: model.isContentBlockingDisabledForCurrentHost ? .orange : Color(red: 0.45, green: 0.68, blue: 1.0)
            ) {
                model.toggleContentBlockingForCurrentHost()
                onDismiss()
            }

        case .desktopSite:
            GlassMenuRow(
                title: model.activeTab?.isDesktopMode == true ? "모바일 사이트 요청" : "데스크톱 사이트 요청",
                systemImage: model.activeTab?.isDesktopMode == true ? "iphone" : "desktopcomputer"
            ) {
                model.toggleDesktopMode()
                onDismiss()
            }

        case .capture:
            GlassMenuRow(title: "실시간 영역 캡처", systemImage: "viewfinder") {
                onDismiss()
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.14) { onOpenCapture() }
            }

        case .watermark:
            GlassMenuRow(title: "워터마크 제거", systemImage: "wand.and.stars") {
                onDismiss()
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.14) { onOpenWatermark() }
            }

        case .mediaPiP:
            HStack(spacing: 6) {
                let secs = model.preferences.pipSeekSeconds
                Button { model.seekActiveMedia(by: -secs) } label: {
                    Label("-\(secs)s", systemImage: "gobackward")
                        .font(.caption2.weight(.semibold))
                        .foregroundStyle(.white)
                        .frame(maxWidth: .infinity)
                        .padding(.vertical, 7)
                        .background(Color.white.opacity(0.10), in: Capsule())
                }
                Button {
                    model.enterActiveVideoPiP()
                    onDismiss()
                } label: {
                    Label("PiP", systemImage: "pip.enter")
                        .font(.caption2.weight(.semibold))
                        .foregroundStyle(.white)
                        .frame(maxWidth: .infinity)
                        .padding(.vertical, 7)
                        .background(Color.white.opacity(0.10), in: Capsule())
                }
                Button { model.seekActiveMedia(by: secs) } label: {
                    Label("+\(secs)s", systemImage: "goforward")
                        .font(.caption2.weight(.semibold))
                        .foregroundStyle(.white)
                        .frame(maxWidth: .infinity)
                        .padding(.vertical, 7)
                        .background(Color.white.opacity(0.10), in: Capsule())
                }
            }
            .buttonStyle(LiquidGlassPressStyle())
            .padding(.horizontal, 14)
            .padding(.vertical, 8)

        case .imagePreloader:
            GlassMenuRow(
                title: "이미지 고속 선로딩",
                systemImage: "bolt.fill",
                iconColor: model.preferences.enableImagePreloader ? Color(red: 0.35, green: 0.65, blue: 1.0) : .white.opacity(0.45),
                trailingText: model.preferences.enableImagePreloader ? "켜짐" : "꺼짐"
            ) {
                model.preferences.enableImagePreloader.toggle()
                model.savePreferences()
                model.applyUserScriptsToAllWebViews()
            }

        case .userScripts:
            ForEach(model.userScripts.prefix(3)) { script in
                GlassMenuRow(
                    title: script.name,
                    systemImage: "puzzlepiece.extension.fill",
                    iconColor: script.isEnabled ? Color(red: 0.35, green: 0.65, blue: 1.0) : .white.opacity(0.4),
                    trailingText: script.isEnabled ? "ON" : "OFF"
                ) {
                    if let idx = model.userScripts.firstIndex(where: { $0.id == script.id }) {
                        model.userScripts[idx].isEnabled.toggle()
                        model.persist()
                        model.applyUserScriptsToAllWebViews()
                    }
                }
            }

        case .extensions:
            GlassMenuRow(title: "확장 프로그램 설정", systemImage: "puzzlepiece.extension") {
                onDismiss()
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.14) { onOpenExtensions() }
            }

        case .bookshelf:
            GlassMenuRow(title: "책장 열기", systemImage: "books.vertical") {
                onDismiss()
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.14) { onToolbarAction(.bookshelf) }
            }

        case .bookmarks:
            GlassMenuRow(title: "북마크 목록", systemImage: "book") {
                onDismiss()
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.14) { onToolbarAction(.bookmarks) }
            }

        case .history:
            GlassMenuRow(title: "방문 기록", systemImage: "clock") {
                onDismiss()
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.14) { onToolbarAction(.history) }
            }

        case .downloads:
            GlassMenuRow(
                title: "다운로드",
                systemImage: "arrow.down.circle",
                trailingText: model.downloads.isEmpty ? nil : "\(model.downloads.count)"
            ) {
                onDismiss()
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.14) { onOpenDownloads() }
            }

        case .copyAddress:
            GlassMenuRow(title: "주소 복사", systemImage: "doc.on.doc") {
                onToolbarAction(.copyAddress)
                onDismiss()
            }

        case .share:
            GlassMenuRow(title: "공유", systemImage: "square.and.arrow.up") {
                onDismiss()
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.14) { onToolbarAction(.share) }
            }

        case .duplicateTab:
            GlassMenuRow(title: "현재 탭 복제", systemImage: "plus.square.on.square") {
                if let active = model.activeTab { model.duplicateTab(active.id) }
                onDismiss()
            }

        case .newTab:
            GlassMenuRow(title: "새 탭", systemImage: "plus.square") {
                onDismiss()
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.14) { onToolbarAction(.newTab) }
            }

        case .reopenClosedTab:
            GlassMenuRow(title: "닫은 탭 다시 열기", systemImage: "arrow.uturn.backward") {
                onToolbarAction(.reopenClosedTab)
                onDismiss()
            }

        case .vpn:
            GlassMenuRow(title: "VPN · 보안 DNS", systemImage: "network.badge.shield.half.filled") {
                onDismiss()
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.14) { onToolbarAction(.vpn) }
            }

        case .driveSync:
            GlassMenuRow(title: "Google Drive 동기화", systemImage: "arrow.triangle.2.circlepath.icloud") {
                onDismiss()
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.14) { onOpenDriveSync() }
            }

        case .settings:
            GlassMenuRow(title: "설정", systemImage: "gearshape") {
                onDismiss()
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.14) { onToolbarAction(.settings) }
            }
        }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            if pinnedTopZoom {
                zoomControlsBar
                    .padding(.vertical, 2)
                Divider().background(Color.white.opacity(0.14))
            }

            ScrollView(showsIndicators: false) {
                VStack(spacing: 0) {
                    ForEach(scrollableItems) { item in
                        rowView(for: item)
                    }
                }
                .padding(.vertical, 4)
            }
            .frame(maxHeight: 440)
        }
        .frame(width: 268)
        .background(
            LiquidGlassShapeBackground(
                shape: RoundedRectangle(cornerRadius: 28, style: .continuous),
                tintOpacity: 0.56,
                rimOpacity: 0.38,
                specularOpacity: 0.22,
                shadowRadius: 22
            )
        )
        .clipShape(RoundedRectangle(cornerRadius: 28, style: .continuous))
    }
}

// MARK: - 2. Bottom-Right Browser Menu Morphing Popover (Android Smart-Filtered Menu in Liquid Glass!)

struct LiquidGlassBrowserMenuPopover: View {
    @ObservedObject var model: BrowserModel
    var onDismiss: () -> Void
    var onAction: (ToolbarShortcutAction) -> Void
    var onOpenCaptureMenu: () -> Void
    var onOpenDownloads: () -> Void
    var onOpenDriveSync: () -> Void
    var onCheckUpdates: () -> Void

    private static let candidateOrder: [ToolbarShortcutAction] = [
        .bookmarks, .history, .capture, .watermark, .bookshelf,
        .vpn, .share, .copyAddress, .reopenClosedTab, .findInPage,
        .desktopSite, .contentBlocking, .extensions, .closeAllTabs, .settings
    ]

    private var filteredEntries: [ToolbarShortcutAction] {
        let pinned = Set(model.preferences.toolbarShortcuts.isEmpty
            ? ToolbarShortcutAction.defaultOrder
            : model.preferences.toolbarShortcuts)
        let list = Self.candidateOrder.filter { !pinned.contains($0) }
        return list.isEmpty ? [.settings] : list
    }

    var body: some View {
        ScrollView(showsIndicators: false) {
            VStack(spacing: 0) {
                ForEach(filteredEntries) { action in
                    GlassMenuRow(
                        title: action.title,
                        systemImage: action.systemImage,
                        iconColor: action == .closeAllTabs ? Color(red: 1.0, green: 0.38, blue: 0.38) : .white,
                        textColor: action == .closeAllTabs ? Color(red: 1.0, green: 0.42, blue: 0.42) : .white
                    ) {
                        onDismiss()
                        DispatchQueue.main.asyncAfter(deadline: .now() + 0.14) {
                            if action == .capture { onOpenCaptureMenu() }
                            else { onAction(action) }
                        }
                    }
                }

                Divider().background(Color.white.opacity(0.14)).padding(.vertical, 2)

                GlassMenuRow(title: "현재 탭 복제", systemImage: "plus.square.on.square") {
                    if let active = model.activeTab { model.duplicateTab(active.id) }
                    onDismiss()
                }
                GlassMenuRow(title: "다운로드", systemImage: "arrow.down.circle", trailingText: model.downloads.isEmpty ? nil : "\(model.downloads.count)") {
                    onDismiss()
                    DispatchQueue.main.asyncAfter(deadline: .now() + 0.14) { onOpenDownloads() }
                }
                GlassMenuRow(title: "Google Drive 동기화", systemImage: "arrow.triangle.2.circlepath.icloud") {
                    onDismiss()
                    DispatchQueue.main.asyncAfter(deadline: .now() + 0.14) { onOpenDriveSync() }
                }
                GlassMenuRow(title: "업데이트 확인", subtitle: "v\(BrowserModel.appVersionName) (build \(BrowserModel.appBuildNumber))", systemImage: "arrow.triangle.2.circlepath") {
                    onDismiss()
                    DispatchQueue.main.asyncAfter(deadline: .now() + 0.14) { onCheckUpdates() }
                }
            }
            .padding(.vertical, 6)
        }
        .frame(width: 262)
        .frame(maxHeight: 440)
        .background(
            LiquidGlassShapeBackground(
                shape: RoundedRectangle(cornerRadius: 28, style: .continuous),
                tintOpacity: 0.56,
                rimOpacity: 0.38,
                specularOpacity: 0.22,
                shadowRadius: 22
            )
        )
        .clipShape(RoundedRectangle(cornerRadius: 28, style: .continuous))
    }
}

// MARK: - 3. Tab Overview Top-Right Menu Popover (Exact Frames 00:22 -> 00:23 -> 00:24 -> 00:25 Inline Profile Expand!)

struct LiquidGlassTabOverviewMenuPopover: View {
    @ObservedObject var model: BrowserModel
    var selectionMode: Bool
    var selectedCount: Int
    var useGridLayout: Bool
    var onDismiss: () -> Void
    var onManageGroups: () -> Void
    var onEnterSelectionMode: () -> Void
    var onSelectAll: () -> Void
    var onMoveSelected: () -> Void
    var onCloseSelected: () -> Void
    var onToggleGridList: () -> Void
    var onCloseOtherTabs: () -> Void
    var onCloseAllTabs: () -> Void

    @State private var profilesExpanded = false

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            if selectionMode {
                GlassMenuRow(title: "전체 선택", systemImage: "checkmark.circle") {
                    onSelectAll()
                    onDismiss()
                }
                GlassMenuRow(title: "선택한 탭 이동 (\(selectedCount))", systemImage: "arrow.right.square") {
                    onDismiss()
                    DispatchQueue.main.asyncAfter(deadline: .now() + 0.14) { onMoveSelected() }
                }
                Divider().background(Color.white.opacity(0.14))
                GlassMenuRow(
                    title: "선택한 탭 닫기 (\(selectedCount))",
                    systemImage: "xmark",
                    iconColor: Color(red: 1.0, green: 0.38, blue: 0.38),
                    textColor: Color(red: 1.0, green: 0.42, blue: 0.42)
                ) {
                    onCloseSelected()
                    onDismiss()
                }
            } else {
                // Exact Frames 00:23 -> 00:24: Expandable Profile Switcher inline inside the Liquid Glass Popover!
                Button {
                    withAnimation(.spring(response: 0.30, dampingFraction: 0.82)) {
                        profilesExpanded.toggle()
                    }
                } label: {
                    HStack(spacing: 12) {
                        Image(systemName: model.activeProfile.icon)
                            .font(.system(size: 16, weight: .semibold))
                            .foregroundStyle(Color(hex: model.activeProfile.colorHex))
                            .frame(width: 24)
                        VStack(alignment: .leading, spacing: 1) {
                            Text("프로필")
                                .font(.system(size: 15, weight: .semibold))
                                .foregroundStyle(.white)
                            Text(model.activeProfile.name)
                                .font(.system(size: 12))
                                .foregroundStyle(.white.opacity(0.65))
                        }
                        Spacer()
                        Image(systemName: profilesExpanded ? "chevron.down" : "chevron.right")
                            .font(.system(size: 12, weight: .semibold))
                            .foregroundStyle(.white.opacity(0.65))
                    }
                    .padding(.horizontal, 16)
                    .padding(.vertical, 12)
                    .contentShape(Rectangle())
                }
                .buttonStyle(LiquidGlassPressStyle())

                if profilesExpanded {
                    VStack(spacing: 0) {
                        ForEach(model.profiles) { profile in
                            let isActive = profile.id == model.activeProfileID
                            Button {
                                withAnimation(.spring(response: 0.30, dampingFraction: 0.84)) {
                                    model.selectProfile(profile.id)
                                }
                                onDismiss()
                            } label: {
                                HStack(spacing: 10) {
                                    Image(systemName: "checkmark")
                                        .font(.system(size: 12, weight: .bold))
                                        .foregroundStyle(.white)
                                        .opacity(isActive ? 1 : 0)
                                        .frame(width: 16)
                                    Image(systemName: profile.icon)
                                        .font(.system(size: 15))
                                        .foregroundStyle(Color(hex: profile.colorHex))
                                        .frame(width: 22)
                                    Text(profile.name)
                                        .font(.system(size: 15, weight: isActive ? .semibold : .regular))
                                        .foregroundStyle(.white)
                                    Spacer()
                                }
                                .padding(.horizontal, 16)
                                .padding(.vertical, 10)
                                .contentShape(Rectangle())
                            }
                            .buttonStyle(LiquidGlassPressStyle())
                        }
                    }
                    .background(Color.white.opacity(0.06))
                    .transition(.opacity.combined(with: .move(edge: .top)))
                }

                Divider().background(Color.white.opacity(0.14))

                GlassMenuRow(title: "탭 그룹 관리", systemImage: "list.bullet") {
                    onDismiss()
                    DispatchQueue.main.asyncAfter(deadline: .now() + 0.14) { onManageGroups() }
                }

                GlassMenuRow(title: "탭 선택", systemImage: "checkmark.circle") {
                    onEnterSelectionMode()
                    onDismiss()
                }

                GlassMenuRow(title: "링크 복사하기", systemImage: "link") {
                    let links = model.visibleTabs.map(\.url).filter { !$0.isEmpty && $0 != "about:blank" }.joined(separator: "\n")
                    if !links.isEmpty {
                        UIPasteboard.general.string = links
                        model.status = "현재 그룹의 탭 링크를 복사했습니다."
                    }
                    onDismiss()
                }

                GlassMenuRow(title: "탭을 북마크에 추가", systemImage: "book") {
                    model.bookmarkAllVisibleTabs()
                    onDismiss()
                }

                GlassMenuRow(
                    title: useGridLayout ? "리스트 보기" : "그리드 보기",
                    systemImage: useGridLayout ? "list.bullet.rectangle" : "square.grid.2x2"
                ) {
                    onToggleGridList()
                    onDismiss()
                }

                if !model.closedTabs.isEmpty {
                    GlassMenuRow(title: "닫은 탭 복원 (\(model.closedTabs.count))", systemImage: "arrow.uturn.backward") {
                        model.restoreClosedTab()
                        onDismiss()
                    }
                }

                Divider().background(Color.white.opacity(0.14))

                if model.visibleTabs.count > 1 {
                    GlassMenuRow(
                        title: "다른 탭 닫기",
                        systemImage: "xmark.circle",
                        iconColor: Color(red: 1.0, green: 0.36, blue: 0.36),
                        textColor: Color(red: 1.0, green: 0.40, blue: 0.40)
                    ) {
                        onCloseOtherTabs()
                        onDismiss()
                    }
                }

                GlassMenuRow(
                    title: "모든 탭 닫기",
                    systemImage: "trash",
                    iconColor: Color(red: 1.0, green: 0.36, blue: 0.36),
                    textColor: Color(red: 1.0, green: 0.40, blue: 0.40)
                ) {
                    onDismiss()
                    DispatchQueue.main.asyncAfter(deadline: .now() + 0.14) { onCloseAllTabs() }
                }
            }
        }
        .padding(.vertical, 6)
        .frame(width: 248)
        .background(
            LiquidGlassShapeBackground(
                shape: RoundedRectangle(cornerRadius: 26, style: .continuous),
                tintOpacity: 0.56,
                rimOpacity: 0.38,
                specularOpacity: 0.22,
                shadowRadius: 22
            )
        )
        .clipShape(RoundedRectangle(cornerRadius: 26, style: .continuous))
    }
}

// MARK: - 4. Tab Card Long-Press Liquid Glass Context Menu (Exact Safari Video Match!)

struct LiquidGlassTabCardContextPopover: View {
    let tab: BrowserTab
    var onDismiss: () -> Void
    var onCopyLink: () -> Void
    var onTogglePin: () -> Void
    var onDuplicate: () -> Void
    var onSelectTab: () -> Void
    var onMoveTab: () -> Void
    var onCloseTab: () -> Void

    private var hasWebURL: Bool {
        let trimmed = tab.url.trimmingCharacters(in: .whitespacesAndNewlines)
        return !trimmed.isEmpty && trimmed != "about:blank"
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            if hasWebURL {
                GlassMenuRow(title: "링크 복사하기", systemImage: "link") {
                    onCopyLink()
                    onDismiss()
                }
            }
            if hasWebURL || tab.isPinned {
                GlassMenuRow(title: tab.isPinned ? "탭 고정 해제" : "탭 고정", systemImage: tab.isPinned ? "pin.slash" : "pin") {
                    onTogglePin()
                    onDismiss()
                }
            }
            GlassMenuRow(title: "탭 복제", systemImage: "plus.square.on.square") {
                onDuplicate()
                onDismiss()
            }
            GlassMenuRow(title: "탭 선택", systemImage: "checkmark.circle") {
                onSelectTab()
                onDismiss()
            }
            GlassMenuRow(title: "탭 이동", systemImage: "arrow.right", trailingChevron: true) {
                onDismiss()
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.16) { onMoveTab() }
            }
            Divider()
                .background(Color.white.opacity(0.14))
                .padding(.vertical, 3)
            GlassMenuRow(
                title: "탭 닫기",
                systemImage: "xmark",
                iconColor: Color(red: 1.0, green: 0.34, blue: 0.34),
                textColor: Color(red: 1.0, green: 0.38, blue: 0.38)
            ) {
                onCloseTab()
                onDismiss()
            }
        }
        .padding(.vertical, 6)
        .frame(width: 244)
        .background(
            LiquidGlassShapeBackground(
                shape: RoundedRectangle(cornerRadius: 26, style: .continuous),
                tintOpacity: 0.64,
                rimOpacity: 0.34,
                specularOpacity: 0.18,
                shadowRadius: 26
            )
        )
        .clipShape(RoundedRectangle(cornerRadius: 26, style: .continuous))
    }
}
