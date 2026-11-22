import SwiftUI
import WebKit
import UIKit
import UniformTypeIdentifiers

// MARK: - Live Webpage Region Capture & Multi-Viewport Scroll-and-Stitch

@MainActor
enum WebpageCaptureEngine {
    static func captureStitchedViewportPages(
        webView: WKWebView,
        viewportCount: Int
    ) async -> UIImage? {
        let count = min(8, max(1, viewportCount))
        // 1. Prepare page: hide thin fixed/sticky headers & ad banners, force eager loading
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
              const state = {
                items: [],
                scrollX: scroller ? scroller.scrollLeft : window.scrollX,
                scrollY: scroller ? scroller.scrollTop : window.scrollY
              };
              window.__omyaruCapState = state;
              for (const img of document.images) {
                try { if (img.loading === 'lazy') img.loading = 'eager'; } catch (_) {}
              }
              const maxBarH = Math.min(160, window.innerHeight * 0.22);
              const maxSideW = Math.min(200, window.innerWidth * 0.22);
              for (const el of document.querySelectorAll('header, nav, footer, aside, [class*="header" i], [class*="footer" i], [class*="navbar" i], [class*="toolbar" i], [style*="fixed" i], [style*="sticky" i]')) {
                if (!(el instanceof HTMLElement)) continue;
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
                state.items.push({ el, style: el.getAttribute('style') });
                el.style.setProperty('visibility', 'hidden', 'important');
              }
              return true;
            })();
            """)

        defer {
            Task { @MainActor in
                _ = try? await webView.evaluateJavaScript("""
                    (() => {
                      const s = window.__omyaruCapState;
                      if (!s) return;
                      s.items?.forEach(({ el, style }) => {
                        if (!el) return;
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
        }

        try? await Task.sleep(nanoseconds: 60_000_000)

        var slices: [UIImage] = []
        let viewportHeight = max(1, webView.bounds.height)

        for index in 0..<count {
            if index > 0 {
                let delta = Double(viewportHeight)
                _ = try? await webView.evaluateJavaScript("""
                    (() => {
                      const scroller = window.__omyaruScroller;
                      if (scroller) scroller.scrollBy(0, \(delta));
                      else window.scrollBy(0, \(delta));
                    })();
                    """)
                try? await Task.sleep(nanoseconds: 140_000_000)
            }
            if let snapshot = await takeSnapshot(of: webView) {
                slices.append(snapshot)
            }
        }

        guard !slices.isEmpty else { return nil }
        if slices.count == 1 { return slices[0] }

        let width = slices[0].size.width
        let totalHeight = slices.reduce(CGFloat.zero) { $0 + $1.size.height }
        let format = UIGraphicsImageRendererFormat()
        format.scale = slices[0].scale
        format.opaque = true
        let renderer = UIGraphicsImageRenderer(size: CGSize(width: width, height: totalHeight), format: format)
        return renderer.image { _ in
            var y: CGFloat = 0
            for slice in slices {
                slice.draw(in: CGRect(x: 0, y: y, width: width, height: slice.size.height))
                y += slice.size.height
            }
        }
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
    @ObservedObject var shelf: ShelfModel = .shared

    @State private var viewportSpan: Int = 1
    @State private var capturedImage: UIImage? = nil
    @State private var loading: Bool = true
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
                bottomActionBar
            }
            .background(Color(red: 0.06, green: 0.06, blue: 0.07))
            .navigationTitle("실시간 영역 캡처")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("취소") { dismiss() }
                }
                ToolbarItem(placement: .topBarTrailing) {
                    Button {
                        cropLeft = 0.0
                        cropTop = 0.0
                        cropRight = 1.0
                        cropBottom = 1.0
                    } label: {
                        Text("전체 선택").font(.subheadline)
                    }
                }
            }
            .task(id: viewportSpan) {
                await refreshCapture()
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
            .onAppear {
                if targetFolder.isEmpty {
                    targetFolder = shelf.activeFolder
                }
            }
        }
        .preferredColorScheme(.dark)
    }

    private var topControlBar: some View {
        HStack(spacing: 12) {
            HStack(spacing: 6) {
                Text("캡처 길이")
                    .font(.caption.weight(.semibold))
                    .foregroundStyle(.secondary)
                Stepper("\(viewportSpan)화면 연속", value: $viewportSpan, in: 1...6)
                    .labelsHidden()
                Text("\(viewportSpan)화면")
                    .font(.caption.weight(.bold))
                    .padding(.horizontal, 8)
                    .padding(.vertical, 4)
                    .background(Color.blue.opacity(0.22), in: Capsule())
            }

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
                    Text("웹페이지 고정 헤더를 숨기고 선명한 화면을 캡처하는 중...")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else if let img = capturedImage {
                GeometryReader { geo in
                    let fitted = fitSize(imageSize: img.size, in: geo.size)
                    let originX = (geo.size.width - fitted.width) / 2
                    let originY = (geo.size.height - fitted.height) / 2
                    let selRect = CGRect(
                        x: originX + fitted.width * cropLeft,
                        y: originY + fitted.height * cropTop,
                        width: fitted.width * (cropRight - cropLeft),
                        height: fitted.height * (cropBottom - cropTop)
                    )

                    ZStack(alignment: .topLeading) {
                        Image(uiImage: img)
                            .resizable()
                            .frame(width: fitted.width, height: fitted.height)
                            .position(x: geo.size.width / 2, y: geo.size.height / 2)

                        // Dimmed mask outside selection
                        Path { path in
                            path.addRect(CGRect(origin: CGPoint(x: originX, y: originY), size: fitted))
                            path.addRect(selRect)
                        }
                        .fill(Color.black.opacity(0.55), style: FillStyle(eoFill: true))

                        // Selection border & handles
                        Rectangle()
                            .strokeBorder(Color.blue, lineWidth: 2)
                            .frame(width: max(4, selRect.width), height: max(4, selRect.height))
                            .position(x: selRect.midX, y: selRect.midY)

                        // Pixel size badge
                        let pxW = Int(img.size.width * img.scale * (cropRight - cropLeft))
                        let pxH = Int(img.size.height * img.scale * (cropBottom - cropTop))
                        Text("▣ \(pxW) × \(pxH)")
                            .font(.caption2.monospacedDigit().weight(.bold))
                            .padding(.horizontal, 8)
                            .padding(.vertical, 4)
                            .background(Color.black.opacity(0.8), in: Capsule())
                            .position(x: selRect.midX, y: max(18, selRect.minY - 14))

                        // Corner handles
                        cornerHandle(at: CGPoint(x: selRect.minX, y: selRect.minY)) { pt in
                            cropLeft = min(cropRight - 0.04, max(0, Double((pt.x - originX) / fitted.width)))
                            cropTop = min(cropBottom - 0.04, max(0, Double((pt.y - originY) / fitted.height)))
                            activeCornerPoint = pt
                        }
                        cornerHandle(at: CGPoint(x: selRect.maxX, y: selRect.minY)) { pt in
                            cropRight = max(cropLeft + 0.04, min(1, Double((pt.x - originX) / fitted.width)))
                            cropTop = min(cropBottom - 0.04, max(0, Double((pt.y - originY) / fitted.height)))
                            activeCornerPoint = pt
                        }
                        cornerHandle(at: CGPoint(x: selRect.minX, y: selRect.maxY)) { pt in
                            cropLeft = min(cropRight - 0.04, max(0, Double((pt.x - originX) / fitted.width)))
                            cropBottom = max(cropTop + 0.04, min(1, Double((pt.y - originY) / fitted.height)))
                            activeCornerPoint = pt
                        }
                        cornerHandle(at: CGPoint(x: selRect.maxX, y: selRect.maxY)) { pt in
                            cropRight = max(cropLeft + 0.04, min(1, Double((pt.x - originX) / fitted.width)))
                            cropBottom = max(cropTop + 0.04, min(1, Double((pt.y - originY) / fitted.height)))
                            activeCornerPoint = pt
                        }

                        // Magnifier lens when dragging a corner
                        if let pt = activeCornerPoint {
                            magnifierLens(image: img, fitted: fitted, origin: CGPoint(x: originX, y: originY), focus: pt)
                        }
                    }
                }
                .padding(12)
            } else {
                VStack(spacing: 10) {
                    Image(systemName: "exclamationmark.triangle")
                        .font(.largeTitle)
                        .foregroundStyle(.orange)
                    Text("화면을 캡처하지 못했습니다.")
                    Button("다시 시도") {
                        Task { await refreshCapture() }
                    }
                    .buttonStyle(.borderedProminent)
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity)
            }
        }
    }

    private var bottomActionBar: some View {
        HStack(spacing: 10) {
            Button {
                guard let cropped = croppedUIImage() else { return }
                UIPasteboard.general.image = cropped
                message = "선택 영역을 클립보드에 복사했습니다."
            } label: {
                Label("복사", systemImage: "doc.on.doc")
                    .font(.subheadline.weight(.semibold))
                    .frame(maxWidth: .infinity)
                    .padding(.vertical, 11)
            }
            .buttonStyle(.bordered)
            .disabled(capturedImage == nil || loading)

            Button {
                guard let cropped = croppedUIImage(), let png = cropped.pngData() else { return }
                UIImageWriteToSavedPhotosAlbum(cropped, nil, nil, nil)
                exportDoc = CapturePNGDocument(data: png)
                exportingPNG = true
            } label: {
                Label("PNG 저장", systemImage: "square.and.arrow.down")
                    .font(.subheadline.weight(.semibold))
                    .frame(maxWidth: .infinity)
                    .padding(.vertical, 11)
            }
            .buttonStyle(.bordered)
            .disabled(capturedImage == nil || loading)

            Button {
                guard let cropped = croppedUIImage(), let png = cropped.pngData() else { return }
                let folder = targetFolder.isEmpty ? shelf.activeFolder : targetFolder
                if shelf.addImageData(png, fileExtension: "png", toFolder: folder, sourceUrl: sourceURL) != nil {
                    dismiss()
                }
            } label: {
                Label("책장에 추가", systemImage: "books.vertical.fill")
                    .font(.subheadline.weight(.semibold))
                    .frame(maxWidth: .infinity)
                    .padding(.vertical, 11)
            }
            .buttonStyle(.borderedProminent)
            .disabled(capturedImage == nil || loading)
        }
        .padding(12)
        .background(Color(red: 0.11, green: 0.11, blue: 0.13))
    }

    private func cornerHandle(at position: CGPoint, onDrag: @escaping (CGPoint) -> Void) -> some View {
        Circle()
            .fill(Color.white)
            .frame(width: 22, height: 22)
            .overlay(Circle().stroke(Color.blue, lineWidth: 3))
            .shadow(radius: 3)
            .position(position)
            .gesture(
                DragGesture(minimumDistance: 0)
                    .onChanged { value in onDrag(value.location) }
                    .onEnded { _ in activeCornerPoint = nil }
            )
    }

    private func magnifierLens(image: UIImage, fitted: CGSize, origin: CGPoint, focus: CGPoint) -> some View {
        let lensSize: CGFloat = 104
        let zoom: CGFloat = 2.4
        let relX = (focus.x - origin.x) / max(1, fitted.width)
        let relY = (focus.y - origin.y) / max(1, fitted.height)
        return ZStack {
            Image(uiImage: image)
                .resizable()
                .frame(width: fitted.width * zoom, height: fitted.height * zoom)
                .offset(
                    x: (0.5 - relX) * fitted.width * zoom,
                    y: (0.5 - relY) * fitted.height * zoom
                )
            Circle().strokeBorder(Color.white, lineWidth: 2.5)
            Path { p in
                p.move(to: CGPoint(x: lensSize / 2, y: 0))
                p.addLine(to: CGPoint(x: lensSize / 2, y: lensSize))
                p.move(to: CGPoint(x: 0, y: lensSize / 2))
                p.addLine(to: CGPoint(x: lensSize, y: lensSize / 2))
            }
            .stroke(Color.blue.opacity(0.8), lineWidth: 1)
        }
        .frame(width: lensSize, height: lensSize)
        .clipShape(Circle())
        .shadow(radius: 8)
        .position(x: max(64, min(origin.x + fitted.width - 64, focus.x)), y: max(64, focus.y - 76))
    }

    private func fitSize(imageSize: CGSize, in container: CGSize) -> CGSize {
        guard imageSize.width > 0, imageSize.height > 0, container.width > 0, container.height > 0 else {
            return container
        }
        let scale = min(container.width / imageSize.width, container.height / imageSize.height)
        return CGSize(width: imageSize.width * scale, height: imageSize.height * scale)
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

    private func refreshCapture() async {
        loading = true
        defer { loading = false }
        guard let webView else { return }
        capturedImage = await WebpageCaptureEngine.captureStitchedViewportPages(webView: webView, viewportCount: viewportSpan)
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
