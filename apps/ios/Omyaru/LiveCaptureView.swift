import SwiftUI
import WebKit
import UIKit
import UniformTypeIdentifiers

// MARK: - Live Webpage Region Capture & Incremental Scroll-and-Stitch Engine (Android MobileCaptureRegionDialog Parity)

@MainActor
enum WebpageCaptureEngine {
    static func preparePageForCapture(webView: WKWebView) async {
        _ = try? await webView.evaluateJavaScript("""
            (() => {
              const detectScroller = () => {
                const doc = document.scrollingElement || document.documentElement;
                let best = null;
                let bestRange = doc ? Math.max(0, doc.scrollHeight - window.innerHeight) : 0;
                for (const el of document.querySelectorAll('div, main, section, article')) {
                  const st = getComputedStyle(el);
                  if (st.overflowY !== 'auto' && st.overflowY !== 'scroll' && st.overflowY !== 'overlay') continue;
                  const range = el.scrollHeight - el.clientHeight;
                  if (range <= 8) continue;
                  const r = el.getBoundingClientRect();
                  if (r.width < window.innerWidth * 0.4 || r.height < window.innerHeight * 0.4) continue;
                  if (range > bestRange) { bestRange = range; best = el; }
                }
                return best;
              };
              const scroller = detectScroller();
              window.__omyaruScroller = scroller;
              if (!window.__omyaruCapState) {
                window.__omyaruCapState = {
                  items: [],
                  scrollX: scroller ? scroller.scrollLeft : window.scrollX,
                  scrollY: scroller ? scroller.scrollTop : window.scrollY
                };
              }
              for (const img of document.images) {
                try { if (img.loading === 'lazy') img.loading = 'eager'; } catch (_) {}
              }
              const maxBarH = Math.min(160, window.innerHeight * 0.22);
              const maxSideW = Math.min(200, window.innerWidth * 0.22);
              for (const el of document.querySelectorAll('header, nav, footer, aside, [class*="header" i], [class*="footer" i], [class*="navbar" i], [class*="toolbar" i], [style*="fixed" i], [style*="sticky" i]')) {
                if (!(el instanceof HTMLElement) || el.dataset.omyaruCapHidden === '1') continue;
                if (el === scroller || (scroller && el.contains(scroller))) continue;
                const cs = getComputedStyle(el);
                if (cs.position !== 'fixed' && cs.position !== 'sticky') continue;
                if (el.scrollHeight > el.clientHeight + 32) continue;
                const r = el.getBoundingClientRect();
                if (r.width <= 0 || r.height <= 0) continue;
                if (r.height > maxBarH && r.width > maxSideW) continue;
                const hasLargeMedia = [...el.querySelectorAll('img, canvas')].some(m => {
                  const mr = m.getBoundingClientRect();
                  return mr.width >= 220 && mr.height >= 180;
                });
                if (hasLargeMedia) continue;
                el.dataset.omyaruCapHidden = '1';
                window.__omyaruCapState.items.push({ el, style: el.getAttribute('style') });
                el.style.setProperty('visibility', 'hidden', 'important');
              }
              return true;
            })();
            """)
        try? await Task.sleep(nanoseconds: 55_000_000)
    }

    static func restorePageAfterCapture(webView: WKWebView) async {
        _ = try? await webView.evaluateJavaScript("""
            (() => {
              const s = window.__omyaruCapState;
              if (!s) return;
              s.items?.forEach(({ el, style }) => {
                if (!el) return;
                delete el.dataset.omyaruCapHidden;
                if (style === null) el.removeAttribute('style');
                else el.setAttribute('style', style);
              });
              const scroller = window.__omyaruScroller;
              if (scroller) { scroller.scrollLeft = s.scrollX || 0; scroller.scrollTop = s.scrollY || 0; }
              else { window.scrollTo(s.scrollX || 0, s.scrollY || 0); }
              delete window.__omyaruCapState;
              delete window.__omyaruScroller;
            })();
            """)
    }

    static func scrollDownAndCaptureSlice(webView: WKWebView) async -> (image: UIImage, movedPoints: CGFloat)? {
        let viewportHeight = max(1, webView.bounds.height)
        let step = Double(viewportHeight * 0.85)
        let movedAny = try? await webView.evaluateJavaScript("""
            (() => {
              const scroller = window.__omyaruScroller;
              const before = scroller ? scroller.scrollTop : window.scrollY;
              if (scroller) scroller.scrollBy(0, \(step));
              else window.scrollBy(0, \(step));
              const after = scroller ? scroller.scrollTop : window.scrollY;
              return Math.max(0, after - before);
            })();
            """)
        let moved = (movedAny as? NSNumber)?.doubleValue ?? 0
        guard moved >= 4 else { return nil }
        await preparePageForCapture(webView: webView)
        try? await Task.sleep(nanoseconds: 140_000_000)
        guard let snap = await takeSnapshot(of: webView) else { return nil }
        return (snap, CGFloat(moved))
    }

    static func appendSlice(base: UIImage, next: UIImage, movedPoints: CGFloat) -> UIImage? {
        let appendHeight = min(next.size.height, movedPoints)
        guard appendHeight >= 2 else { return nil }
        let width = base.size.width
        let newTotalHeight = base.size.height + appendHeight
        guard width * newTotalHeight * base.scale * base.scale <= 24_000_000 else { return nil }

        let format = UIGraphicsImageRendererFormat()
        format.scale = base.scale
        format.opaque = true
        let renderer = UIGraphicsImageRenderer(size: CGSize(width: width, height: newTotalHeight), format: format)
        return renderer.image { _ in
            base.draw(in: CGRect(x: 0, y: 0, width: width, height: base.size.height))
            if let cg = next.cgImage {
                let scale = next.scale
                let cropPxH = min(CGFloat(cg.height), appendHeight * scale)
                let cropRect = CGRect(x: 0, y: CGFloat(cg.height) - cropPxH, width: CGFloat(cg.width), height: cropPxH)
                if let sliceCG = cg.cropping(to: cropRect) {
                    UIImage(cgImage: sliceCG, scale: scale, orientation: next.imageOrientation)
                        .draw(in: CGRect(x: 0, y: base.size.height, width: width, height: appendHeight))
                }
            }
        }
    }

    static func captureStitchedViewportPages(webView: WKWebView, viewportCount: Int) async -> UIImage? {
        await preparePageForCapture(webView: webView)
        defer {
            Task { @MainActor in
                await restorePageAfterCapture(webView: webView)
            }
        }
        guard var current = await takeSnapshot(of: webView) else { return nil }
        let extra = min(7, max(0, viewportCount - 1))
        for _ in 0..<extra {
            guard let (next, moved) = await scrollDownAndCaptureSlice(webView: webView),
                  let combined = appendSlice(base: current, next: next, movedPoints: moved) else { break }
            current = combined
        }
        return current
    }

    static func takeSnapshot(of webView: WKWebView) async -> UIImage? {
        await withCheckedContinuation { continuation in
            let config = WKSnapshotConfiguration()
            webView.takeSnapshot(with: config) { image, _ in
                continuation.resume(returning: image)
            }
        }
    }
}

// MARK: - Interactive Live Capture View

struct LiveCaptureView: View {
    @Environment(\.dismiss) private var dismiss
    let webView: WKWebView?
    let sourceURL: String
    var sideMargins: Bool = true
    var onSendToWatermark: ((UIImage) -> Void)? = nil
    @ObservedObject var shelf: ShelfModel = .shared

    @State private var capturedImage: UIImage? = nil
    @State private var loading: Bool = true
    @State private var extending: Bool = false
    @State private var extendedPagesCount: Int = 1
    @State private var cropLeft: Double = 0.04
    @State private var cropTop: Double = 0.04
    @State private var cropRight: Double = 0.96
    @State private var cropBottom: Double = 0.96
    @State private var targetFolder: String = ""
    @State private var showFolderPicker = false
    @State private var activeCornerPoint: CGPoint? = nil
    @State private var message: String? = nil
    @State private var exportingPNG = false
    @State private var exportDoc = CapturePNGDocument(data: Data())

    var body: some View {
        NavigationStack {
            VStack(spacing: 0) {
                topControlBar
                captureCanvas
                extendDownwardButton
                bottomActionBar
            }
            .background(Color(red: 0.06, green: 0.06, blue: 0.07))
            .navigationTitle("실시간 영역 캡처")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("취소") {
                        cleanupAndDismiss()
                    }
                }
                ToolbarItem(placement: .topBarTrailing) {
                    Button("전체 선택") {
                        cropLeft = 0.0
                        cropTop = 0.0
                        cropRight = 1.0
                        cropBottom = 1.0
                    }
                    .font(.subheadline)
                }
            }
            .task {
                if targetFolder.isEmpty { targetFolder = shelf.activeFolder }
                await initialCapture()
            }
            .onDisappear {
                if let webView {
                    Task { @MainActor in
                        await WebpageCaptureEngine.restorePageAfterCapture(webView: webView)
                    }
                }
            }
            .sheet(isPresented: $showFolderPicker) {
                CaptureFolderPickerSheet(shelf: shelf, selectedFolder: $targetFolder)
            }
            .fileExporter(
                isPresented: $exportingPNG,
                document: exportDoc,
                contentType: .png,
                defaultFilename: "Omyaru-capture-\(Int(Date().timeIntervalSince1970)).png"
            ) { result in
                if case .success = result {
                    message = "파일에 PNG 저장을 완료했습니다."
                }
            }
            .alert("영역 캡처", isPresented: Binding(get: { message != nil }, set: { if !$0 { message = nil } })) {
                Button("확인") { message = nil }
            } message: {
                Text(message ?? "")
            }
        }
        .preferredColorScheme(.dark)
    }

    private var topControlBar: some View {
        HStack(spacing: 10) {
            Text("모서리 손잡이로 조절 · \(extendedPagesCount)화면")
                .font(.caption.weight(.semibold))
                .foregroundStyle(.secondary)
            Spacer()
            Button {
                showFolderPicker = true
            } label: {
                HStack(spacing: 4) {
                    Image(systemName: "folder.fill").foregroundStyle(.blue)
                    Text(BookshelfPath.leaf(targetFolder.isEmpty ? shelf.activeFolder : targetFolder))
                        .lineLimit(1)
                    Image(systemName: "chevron.down").font(.caption2)
                }
                .font(.caption.weight(.semibold))
                .padding(.horizontal, 10)
                .padding(.vertical, 6)
                .background(Color(red: 0.16, green: 0.16, blue: 0.18), in: Capsule())
            }
            .buttonStyle(.plain)
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 8)
        .background(Color(red: 0.11, green: 0.11, blue: 0.12))
    }

    private var captureCanvas: some View {
        ZStack {
            if loading {
                VStack(spacing: 12) {
                    ProgressView()
                    Text("고정 헤더를 숨기고 선명한 웹 화면을 캡처하는 중...")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else if let img = capturedImage {
                GeometryReader { geo in
                    let hMargin: CGFloat = sideMargins ? 20 : 0
                    let availW = max(60, geo.size.width - hMargin * 2)
                    let displayH = max(120, availW * (img.size.height / max(1, img.size.width)))
                    let displaySize = CGSize(width: availW, height: displayH)

                    ScrollViewReader { proxy in
                        ScrollView(.vertical, showsIndicators: true) {
                            ZStack(alignment: .topLeading) {
                                Image(uiImage: img)
                                    .resizable()
                                    .frame(width: displaySize.width, height: displaySize.height)

                                let selRect = CGRect(
                                    x: displaySize.width * cropLeft,
                                    y: displaySize.height * cropTop,
                                    width: displaySize.width * (cropRight - cropLeft),
                                    height: displaySize.height * (cropBottom - cropTop)
                                )

                                Path { path in
                                    path.addRect(CGRect(origin: .zero, size: displaySize))
                                    path.addRect(selRect)
                                }
                                .fill(Color.black.opacity(0.54), style: FillStyle(eoFill: true))

                                Rectangle()
                                    .strokeBorder(Color.cyan, lineWidth: 2)
                                    .frame(width: max(4, selRect.width), height: max(4, selRect.height))
                                    .position(x: selRect.midX, y: selRect.midY)

                                let pxW = Int(img.size.width * img.scale * (cropRight - cropLeft))
                                let pxH = Int(img.size.height * img.scale * (cropBottom - cropTop))
                                Text("▣ \(pxW) × \(pxH)")
                                    .font(.caption2.monospacedDigit().weight(.bold))
                                    .padding(.horizontal, 8)
                                    .padding(.vertical, 4)
                                    .background(Color.black.opacity(0.8), in: Capsule())
                                    .position(x: selRect.midX, y: max(18, selRect.minY - 14))

                                cornerHandle(at: CGPoint(x: selRect.minX, y: selRect.minY)) { pt in
                                    cropLeft = min(cropRight - 0.04, max(0, Double(pt.x / displaySize.width)))
                                    cropTop = min(cropBottom - 0.04, max(0, Double(pt.y / displaySize.height)))
                                    activeCornerPoint = pt
                                }
                                cornerHandle(at: CGPoint(x: selRect.maxX, y: selRect.minY)) { pt in
                                    cropRight = max(cropLeft + 0.04, min(1, Double(pt.x / displaySize.width)))
                                    cropTop = min(cropBottom - 0.04, max(0, Double(pt.y / displaySize.height)))
                                    activeCornerPoint = pt
                                }
                                cornerHandle(at: CGPoint(x: selRect.minX, y: selRect.maxY)) { pt in
                                    cropLeft = min(cropRight - 0.04, max(0, Double(pt.x / displaySize.width)))
                                    cropBottom = max(cropTop + 0.04, min(1, Double(pt.y / displaySize.height)))
                                    activeCornerPoint = pt
                                }
                                cornerHandle(at: CGPoint(x: selRect.maxX, y: selRect.maxY)) { pt in
                                    cropRight = max(cropLeft + 0.04, min(1, Double(pt.x / displaySize.width)))
                                    cropBottom = max(cropTop + 0.04, min(1, Double(pt.y / displaySize.height)))
                                    activeCornerPoint = pt
                                }

                                if let pt = activeCornerPoint {
                                    magnifierLens(image: img, displaySize: displaySize, focus: pt)
                                }
                            }
                            .frame(width: displaySize.width, height: displaySize.height)
                            .padding(.horizontal, hMargin)
                            .padding(.vertical, 10)
                            Color.clear.frame(height: 1).id("__capture_bottom__")
                        }
                        .onChange(of: extendedPagesCount) { _ in
                            withAnimation(.easeOut(duration: 0.25)) {
                                proxy.scrollTo("__capture_bottom__", anchor: .bottom)
                            }
                        }
                    }
                }
            } else {
                VStack(spacing: 10) {
                    Image(systemName: "exclamationmark.triangle")
                        .font(.largeTitle)
                        .foregroundStyle(.orange)
                    Text("화면을 캡처하지 못했습니다.")
                    Button("다시 시도") { Task { await initialCapture() } }
                        .buttonStyle(.borderedProminent)
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity)
            }
        }
    }

    private var extendDownwardButton: some View {
        Button {
            Task { await extendDownward() }
        } label: {
            HStack(spacing: 8) {
                if extending {
                    ProgressView().scaleEffect(0.8)
                } else {
                    Image(systemName: "arrow.down.to.line")
                }
                Text("↓ 아래로 확장 (페이지를 스크롤하여 이어 붙이기)")
                    .font(.subheadline.weight(.semibold))
            }
            .frame(maxWidth: .infinity)
            .padding(.vertical, 10)
            .background(Color(red: 0.16, green: 0.16, blue: 0.19), in: RoundedRectangle(cornerRadius: 10))
        }
        .buttonStyle(.plain)
        .disabled(loading || extending || capturedImage == nil || webView == nil)
        .padding(.horizontal, 12)
        .padding(.top, 8)
    }

    private var bottomActionBar: some View {
        VStack(spacing: 8) {
            HStack(spacing: 8) {
                Button {
                    guard let cropped = croppedUIImage() else { return }
                    UIPasteboard.general.image = cropped
                    message = "선택 영역을 클립보드에 복사했습니다."
                } label: {
                    Label("복사", systemImage: "doc.on.doc")
                        .font(.caption.weight(.semibold))
                        .frame(maxWidth: .infinity)
                        .padding(.vertical, 10)
                }
                .buttonStyle(.bordered)

                Button {
                    guard let cropped = croppedUIImage(), let png = cropped.pngData() else { return }
                    UIImageWriteToSavedPhotosAlbum(cropped, nil, nil, nil)
                    exportDoc = CapturePNGDocument(data: png)
                    exportingPNG = true
                } label: {
                    Label("PNG 저장", systemImage: "square.and.arrow.down")
                        .font(.caption.weight(.semibold))
                        .frame(maxWidth: .infinity)
                        .padding(.vertical, 10)
                }
                .buttonStyle(.bordered)

                if let onSendToWatermark {
                    Button {
                        guard let cropped = croppedUIImage() else { return }
                        cleanupAndDismiss()
                        onSendToWatermark(cropped)
                    } label: {
                        Label("워터마크", systemImage: "wand.and.stars")
                            .font(.caption.weight(.semibold))
                            .frame(maxWidth: .infinity)
                            .padding(.vertical, 10)
                    }
                    .buttonStyle(.bordered)
                }

                Button {
                    guard let cropped = croppedUIImage(), let png = cropped.pngData() else { return }
                    let folder = targetFolder.isEmpty ? shelf.activeFolder : targetFolder
                    if shelf.addImageData(png, fileExtension: "png", toFolder: folder, sourceUrl: sourceURL) != nil {
                        cleanupAndDismiss()
                    }
                } label: {
                    Label("책장 추가", systemImage: "books.vertical.fill")
                        .font(.caption.weight(.semibold))
                        .frame(maxWidth: .infinity)
                        .padding(.vertical, 10)
                }
                .buttonStyle(.borderedProminent)
            }
            .disabled(capturedImage == nil || loading)
        }
        .padding(12)
        .background(Color(red: 0.11, green: 0.11, blue: 0.13))
    }

    private func cornerHandle(at position: CGPoint, onDrag: @escaping (CGPoint) -> Void) -> some View {
        Circle()
            .fill(Color.white)
            .frame(width: 24, height: 24)
            .overlay(Circle().stroke(Color.cyan, lineWidth: 3))
            .shadow(radius: 3)
            .position(position)
            .gesture(
                DragGesture(minimumDistance: 0)
                    .onChanged { value in onDrag(value.location) }
                    .onEnded { _ in activeCornerPoint = nil }
            )
    }

    private func magnifierLens(image: UIImage, displaySize: CGSize, focus: CGPoint) -> some View {
        let lensSize: CGFloat = 104
        let zoom: CGFloat = 2.4
        let relX = focus.x / max(1, displaySize.width)
        let relY = focus.y / max(1, displaySize.height)
        return ZStack {
            Image(uiImage: image)
                .resizable()
                .frame(width: displaySize.width * zoom, height: displaySize.height * zoom)
                .offset(
                    x: (0.5 - relX) * displaySize.width * zoom,
                    y: (0.5 - relY) * displaySize.height * zoom
                )
            Circle().strokeBorder(Color.white, lineWidth: 2.5)
            Path { p in
                p.move(to: CGPoint(x: lensSize / 2, y: 0))
                p.addLine(to: CGPoint(x: lensSize / 2, y: lensSize))
                p.move(to: CGPoint(x: 0, y: lensSize / 2))
                p.addLine(to: CGPoint(x: lensSize, y: lensSize / 2))
            }
            .stroke(Color.cyan.opacity(0.85), lineWidth: 1)
        }
        .frame(width: lensSize, height: lensSize)
        .clipShape(Circle())
        .shadow(radius: 8)
        .position(x: max(60, min(displaySize.width - 60, focus.x)), y: max(60, focus.y - 76))
    }

    private func croppedUIImage() -> UIImage? {
        guard let img = capturedImage, let cg = img.cgImage else { return nil }
        let w = Double(cg.width)
        let h = Double(cg.height)
        let rect = CGRect(
            x: w * cropLeft,
            y: h * cropTop,
            width: max(2, w * (cropRight - cropLeft)),
            height: max(2, h * (cropBottom - cropTop))
        ).integral
        guard let croppedCG = cg.cropping(to: rect) else { return nil }
        return UIImage(cgImage: croppedCG, scale: img.scale, orientation: img.imageOrientation)
    }

    private func initialCapture() async {
        loading = true
        defer { loading = false }
        guard let webView else { return }
        await WebpageCaptureEngine.preparePageForCapture(webView: webView)
        capturedImage = await WebpageCaptureEngine.takeSnapshot(of: webView)
        extendedPagesCount = 1
    }

    private func extendDownward() async {
        guard let webView, let base = capturedImage, !extending else { return }
        extending = true
        defer { extending = false }
        guard let (next, moved) = await WebpageCaptureEngine.scrollDownAndCaptureSlice(webView: webView) else {
            message = "페이지 최하단에 도달했거나 더 내려갈 수 없습니다."
            return
        }
        guard let combined = WebpageCaptureEngine.appendSlice(base: base, next: next, movedPoints: moved) else {
            message = "최대 캡처 크기 제한에 도달했습니다. 현재 영역을 먼저 저장해 주세요."
            return
        }
        let ratio = Double(base.size.height / max(1, combined.size.height))
        cropTop = cropTop * ratio
        cropBottom = 0.98
        capturedImage = combined
        extendedPagesCount += 1
    }

    private func cleanupAndDismiss() {
        if let webView {
            Task { @MainActor in
                await WebpageCaptureEngine.restorePageAfterCapture(webView: webView)
            }
        }
        dismiss()
    }
}

// MARK: - Capture Folder Picker Sheet (With Explicit Confirm Button!)

struct CaptureFolderPickerSheet: View {
    @Environment(\.dismiss) private var dismiss
    @ObservedObject var shelf: ShelfModel
    @Binding var selectedFolder: String
    @State private var pendingFolder: String = ""
    @State private var showNewFolder = false
    @State private var newFolderName = ""

    var body: some View {
        NavigationStack {
            VStack(spacing: 0) {
                List(shelf.allDisplayFolders, id: \.self) { folder in
                    let level = folder.filter { $0 == "/" }.count
                    let isSelected = pendingFolder.caseInsensitiveCompare(folder) == .orderedSame
                    HStack {
                        if level > 0 { Spacer().frame(width: CGFloat(level) * 18) }
                        Button {
                            pendingFolder = folder
                        } label: {
                            HStack {
                                Image(systemName: isSelected ? "folder.fill" : "folder")
                                    .foregroundStyle(isSelected ? .blue : .secondary)
                                Text(BookshelfPath.leaf(folder))
                                    .font(.body.weight(isSelected ? .semibold : .regular))
                                Text("(\(shelf.visibleItems(in: folder).count))")
                                    .font(.caption)
                                    .foregroundStyle(.secondary)
                                Spacer()
                            }
                        }
                        .buttonStyle(.plain)

                        Button("선택") {
                            selectedFolder = folder
                            dismiss()
                        }
                        .buttonStyle(.borderedProminent)
                        .controlSize(.small)
                    }
                }
                .listStyle(.plain)

                HStack(spacing: 10) {
                    Button {
                        newFolderName = ""
                        showNewFolder = true
                    } label: {
                        Label("새 폴더", systemImage: "folder.badge.plus")
                            .frame(maxWidth: .infinity)
                            .padding(.vertical, 10)
                    }
                    .buttonStyle(.bordered)

                    Button {
                        if !pendingFolder.isEmpty {
                            selectedFolder = pendingFolder
                        }
                        dismiss()
                    } label: {
                        Label("확인 (‘\(BookshelfPath.leaf(pendingFolder))’)", systemImage: "checkmark.circle.fill")
                            .frame(maxWidth: .infinity)
                            .padding(.vertical, 10)
                    }
                    .buttonStyle(.borderedProminent)
                }
                .padding(12)
                .background(Color(red: 0.11, green: 0.11, blue: 0.13))
            }
            .navigationTitle("저장할 책장 폴더")
            .navigationBarTitleDisplayMode(.inline)
            .onAppear {
                pendingFolder = selectedFolder.isEmpty ? shelf.activeFolder : selectedFolder
            }
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("취소") { dismiss() }
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button("확인") {
                        if !pendingFolder.isEmpty { selectedFolder = pendingFolder }
                        dismiss()
                    }
                }
            }
            .alert("새 책장 폴더", isPresented: $showNewFolder) {
                TextField("폴더 이름", text: $newFolderName)
                Button("만들기") {
                    let trimmed = newFolderName.trimmingCharacters(in: .whitespacesAndNewlines)
                    guard !trimmed.isEmpty else { return }
                    let path = pendingFolder.isEmpty ? trimmed : "\(pendingFolder)/\(trimmed)"
                    if let created = shelf.addFolder(path, select: false) {
                        pendingFolder = created
                        selectedFolder = created
                    }
                }
                Button("취소", role: .cancel) {}
            }
        }
        .preferredColorScheme(.dark)
    }
}

struct CapturePNGDocument: FileDocument {
    static var readableContentTypes: [UTType] { [.png] }
    var data: Data
    init(data: Data) { self.data = data }
    init(configuration: ReadConfiguration) throws {
        data = configuration.file.regularFileContents ?? Data()
    }
    func fileWrapper(configuration: WriteConfiguration) throws -> FileWrapper {
        FileWrapper(regularFileWithContents: data)
    }
}
